namespace TPR10.Api.Data.Migrations;

// Frozen SQL: these guards belong to this migration, not to mutable application services.
public partial class AddAttendanceCorrections
{
    private const string CorrectionGuards = """
        CREATE FUNCTION correction_write_marker() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN NEW.written_transaction_id := txid_current(); RETURN NEW; END $$;
        CREATE TRIGGER correction_write_marker BEFORE INSERT OR UPDATE ON attendance_days FOR EACH ROW EXECUTE FUNCTION correction_write_marker();
        CREATE TRIGGER correction_write_marker BEFORE INSERT ON attendance_events FOR EACH ROW EXECUTE FUNCTION correction_write_marker();
        CREATE TRIGGER correction_write_marker BEFORE INSERT ON attendance_adjustments FOR EACH ROW EXECUTE FUNCTION correction_write_marker();
        CREATE FUNCTION correction_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Correction history is immutable' USING ERRCODE='23514'; END $$;
        DO $$ DECLARE tab text; BEGIN
            FOREACH tab IN ARRAY ARRAY['correction_requests','correction_revisions','correction_route_snapshots',
                'correction_approval_actions','attendance_adjustments','correction_command_receipts'] LOOP
                EXECUTE format('CREATE TRIGGER correction_write_lock BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH STATEMENT EXECUTE FUNCTION attendance_recording_write_lock()',tab);
                EXECUTE format('CREATE TRIGGER correction_no_delete BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION correction_immutable()',tab);
                EXECUTE format('CREATE TRIGGER correction_no_truncate BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION correction_immutable()',tab);
                IF tab<>'correction_requests' THEN
                    EXECUTE format('CREATE TRIGGER correction_no_update BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION correction_immutable()',tab);
                END IF;
            END LOOP;
        END $$;

        CREATE FUNCTION correction_preserve_request() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='INSERT' THEN
                IF NEW.version<>1 OR NEW.state<>'Draft' OR NEW.current_revision<>0 OR NEW.deadline_utc<>NEW.deadline_anchor_utc THEN
                    RAISE EXCEPTION 'Request must start as a fresh draft' USING ERRCODE='23514';
                END IF;
                IF EXISTS(SELECT 1 FROM correction_requests WHERE employee_id=NEW.employee_id
                    AND original_work_date=NEW.original_work_date AND state='Expired') THEN
                    RAISE EXCEPTION 'Expired correction cannot be recreated' USING ERRCODE='23514';
                END IF;
            ELSE
                IF ROW(NEW.id,NEW.employee_id,NEW.original_work_date,NEW.day_id,NEW.membership_id,NEW.workspace_id,
                    NEW.department_id,NEW.snapshot_at_utc,NEW.deadline_anchor_utc)
                    IS DISTINCT FROM ROW(OLD.id,OLD.employee_id,OLD.original_work_date,OLD.day_id,OLD.membership_id,OLD.workspace_id,
                    OLD.department_id,OLD.snapshot_at_utc,OLD.deadline_anchor_utc)
                    OR NEW.version<>OLD.version+1 OR OLD.state IN ('Approved','Expired') THEN
                    RAISE EXCEPTION 'Correction identity, anchor and terminal history are immutable' USING ERRCODE='23514';
                END IF;
                IF NEW.current_revision<OLD.current_revision OR NEW.current_revision>OLD.current_revision+1 THEN
                    RAISE EXCEPTION 'Revision must advance monotonically' USING ERRCODE='23514';
                END IF;
                IF NEW.deadline_utc<>OLD.deadline_utc AND NOT (
                    OLD.state IN ('PendingSupervisor','PendingHr') AND NEW.state='Returned' AND EXISTS (
                        SELECT 1 FROM correction_approval_actions a WHERE a.request_id=NEW.id AND a.revision=OLD.current_revision
                        AND a.outcome='Returned' AND NEW.deadline_utc=(((a.occurred_at_utc AT TIME ZONE 'Asia/Bangkok')::date+8)::timestamp AT TIME ZONE 'Asia/Bangkok'))) THEN
                    RAISE EXCEPTION 'Only a returned revision can renew the deadline' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER correction_request_guard BEFORE INSERT OR UPDATE ON correction_requests FOR EACH ROW EXECUTE FUNCTION correction_preserve_request();

        CREATE FUNCTION correction_revision_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.revision<>COALESCE((SELECT max(revision)+1 FROM correction_revisions WHERE request_id=NEW.request_id),1) THEN
                RAISE EXCEPTION 'Revision number must be the next number' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER correction_revision_guard BEFORE INSERT ON correction_revisions FOR EACH ROW EXECUTE FUNCTION correction_revision_guard();

        CREATE FUNCTION correction_route_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE owner_id uuid;
        BEGIN
            SELECT employee_id INTO owner_id FROM correction_requests WHERE id=NEW.request_id;
            IF NEW.supervisor_id=owner_id OR owner_id=ANY(NEW.hr_candidate_ids) OR NEW.supervisor_id=ANY(NEW.hr_candidate_ids)
                OR array_position(NEW.hr_candidate_ids,NULL) IS NOT NULL
                OR EXISTS(SELECT 1 FROM unnest(NEW.hr_candidate_ids) c WHERE NOT EXISTS(SELECT 1 FROM users WHERE id=c)) THEN
                RAISE EXCEPTION 'Route requires distinct existing reviewers and owner' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER correction_route_guard BEFORE INSERT ON correction_route_snapshots FOR EACH ROW EXECUTE FUNCTION correction_route_guard();

        CREATE FUNCTION correction_request_completion() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.state='Approved' AND NOT EXISTS(SELECT 1 FROM attendance_adjustments WHERE request_id=NEW.id AND revision=NEW.current_revision
                AND written_transaction_id=txid_current()) THEN
                RAISE EXCEPTION 'Approval must atomically apply an adjustment' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER correction_request_completion AFTER UPDATE ON correction_requests
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION correction_request_completion();

        CREATE FUNCTION correction_validate_adjustment() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM correction_requests r
                JOIN correction_revisions v ON v.request_id=r.id AND v.revision=NEW.revision
                JOIN correction_route_snapshots route ON route.request_id=r.id AND route.revision=v.revision
                JOIN correction_approval_actions s ON s.request_id=r.id AND s.revision=v.revision AND s.step='Supervisor' AND s.outcome='Approved'
                JOIN correction_approval_actions h ON h.request_id=r.id AND h.revision=v.revision AND h.step='Hr' AND h.outcome='Approved'
                JOIN attendance_days d ON d.id=NEW.day_id
                WHERE r.id=NEW.request_id AND r.state='Approved' AND r.current_revision=v.revision
                  AND s.actor_id=route.supervisor_id AND h.actor_id=ANY(route.hr_candidate_ids)
                  AND s.actor_id<>h.actor_id AND s.actor_id<>r.employee_id AND h.actor_id<>r.employee_id
                  AND s.occurred_at_utc<=h.occurred_at_utc AND h.occurred_at_utc<=NEW.applied_at_utc
                  AND v.baseline_day_version IS NOT DISTINCT FROM NEW.before_version
                  AND ((r.day_id=NEW.day_id AND NEW.before_version IS NOT NULL) OR (r.day_id IS NULL AND NEW.before_version IS NULL))
                  AND ROW(d.employee_id,d.membership_id,d.workspace_id,d.department_id,d.snapshot_at_utc)
                      IS NOT DISTINCT FROM ROW(r.employee_id,r.membership_id,r.workspace_id,r.department_id,r.snapshot_at_utc)
                  AND ROW(v.work_date,v.in_utc,v.out_utc) IS NOT DISTINCT FROM ROW(NEW.after_work_date,NEW.after_in_utc,NEW.after_out_utc)
                  AND ROW(d.work_date,d.effective_in_utc,d.effective_out_utc,d.version)
                      IS NOT DISTINCT FROM ROW(NEW.after_work_date,NEW.after_in_utc,NEW.after_out_utc,NEW.after_version)
                  AND d.written_transaction_id=txid_current()
            ) THEN
                RAISE EXCEPTION 'Adjustment requires exact proposal, distinct approvals and atomic day change' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER correction_adjustment_guard AFTER INSERT ON attendance_adjustments
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION correction_validate_adjustment();

        CREATE OR REPLACE FUNCTION attendance_preserve_day() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.employee_id,NEW.membership_id,NEW.workspace_id,NEW.department_id,NEW.snapshot_at_utc)
                    IS DISTINCT FROM ROW(OLD.id,OLD.employee_id,OLD.membership_id,OLD.workspace_id,OLD.department_id,OLD.snapshot_at_utc)
                    OR NEW.version<>OLD.version+1 THEN
                    RAISE EXCEPTION 'Attendance snapshot immutable; version must advance once' USING ERRCODE='23514';
                END IF;
            END IF;
            IF EXISTS(SELECT 1 FROM employee_memberships m WHERE m.id=NEW.membership_id AND m.user_id=NEW.employee_id
                AND m.workspace_id=NEW.workspace_id AND m.department_id=NEW.department_id
                AND NOT (m.valid_from_utc<=NEW.snapshot_at_utc AND (m.valid_to_utc IS NULL OR NEW.snapshot_at_utc<m.valid_to_utc))) THEN
                RAISE EXCEPTION 'Attendance day snapshot outside membership period' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE FUNCTION correction_validate_day_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW.work_date,NEW.effective_in_utc,NEW.effective_out_utc,NEW.version) IS NOT DISTINCT FROM ROW(OLD.work_date,OLD.effective_in_utc,OLD.effective_out_utc,OLD.version) THEN
                RETURN NULL;
            END IF;
            -- The ordinary live checkout path still requires a new immutable evidence-backed event.
            IF NEW.work_date=OLD.work_date AND NEW.effective_in_utc=OLD.effective_in_utc AND OLD.effective_out_utc IS NULL
                AND EXISTS(SELECT 1 FROM attendance_events e WHERE e.day_id=NEW.id AND e.action='CheckOut'
                    AND e.occurred_at_utc=NEW.effective_out_utc AND e.written_transaction_id=txid_current()) THEN
                RETURN NULL;
            END IF;
            IF NOT EXISTS(SELECT 1 FROM attendance_adjustments a WHERE a.day_id=NEW.id
                AND a.written_transaction_id=txid_current()
                AND ROW(a.before_version,a.before_work_date,a.before_in_utc,a.before_out_utc)
                    IS NOT DISTINCT FROM ROW(OLD.version,OLD.work_date,OLD.effective_in_utc,OLD.effective_out_utc)
                AND ROW(a.after_version,a.after_work_date,a.after_in_utc,a.after_out_utc)
                    IS NOT DISTINCT FROM ROW(NEW.version,NEW.work_date,NEW.effective_in_utc,NEW.effective_out_utc)) THEN
                RAISE EXCEPTION 'Effective change requires exact approved adjustment' USING ERRCODE='23514';
            END IF;
            IF NOT EXISTS(SELECT 1 FROM employee_memberships m WHERE m.id=NEW.membership_id
                AND m.valid_from_utc<=NEW.effective_in_utc AND (m.valid_to_utc IS NULL OR NEW.effective_in_utc<m.valid_to_utc)) THEN
                RAISE EXCEPTION 'Correction cannot change historical membership' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER correction_day_change_guard AFTER UPDATE ON attendance_days
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION correction_validate_day_change();
        """;

    private const string CorrectionDownPreflight = """
        LOCK TABLE correction_requests, correction_revisions, correction_route_snapshots, correction_approval_actions,
            attendance_adjustments, correction_command_receipts, attendance_days IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM correction_requests) OR EXISTS(SELECT 1 FROM attendance_adjustments) THEN
                RAISE EXCEPTION 'Cannot downgrade while retained correction history exists';
            END IF;
        END $$;
        DROP TRIGGER correction_day_change_guard ON attendance_days;
        DROP TRIGGER correction_write_marker ON attendance_days;
        DROP TRIGGER correction_write_marker ON attendance_events;
        DROP TRIGGER correction_write_marker ON attendance_adjustments;
        DROP FUNCTION correction_write_marker();
        DROP FUNCTION correction_validate_day_change();
        CREATE OR REPLACE FUNCTION attendance_preserve_day() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.employee_id,NEW.work_date,NEW.membership_id,NEW.workspace_id,NEW.department_id,NEW.snapshot_at_utc)
                    IS DISTINCT FROM ROW(OLD.id,OLD.employee_id,OLD.work_date,OLD.membership_id,OLD.workspace_id,OLD.department_id,OLD.snapshot_at_utc)
                    OR (NEW IS DISTINCT FROM OLD AND NEW.version<=OLD.version) THEN
                    RAISE EXCEPTION 'Attendance day snapshot is immutable and version must advance' USING ERRCODE='23514';
                END IF;
            END IF;
            IF EXISTS(SELECT 1 FROM employee_memberships m WHERE m.id=NEW.membership_id AND m.user_id=NEW.employee_id
                AND m.workspace_id=NEW.workspace_id AND m.department_id=NEW.department_id
                AND NOT (m.valid_from_utc<=NEW.snapshot_at_utc AND (m.valid_to_utc IS NULL OR NEW.snapshot_at_utc<m.valid_to_utc))) THEN
                RAISE EXCEPTION 'Attendance day snapshot outside membership period' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        """;
}
