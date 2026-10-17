namespace TPR10.Api.Data.Migrations;

// Frozen migration SQL. Application services must not be called from historical migrations.
public partial class AddAttendanceRecording
{
    private const string RecordingDownPreflight = """
        LOCK TABLE attendance_days, attendance_events, capture_challenges, attendance_operations IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM attendance_days) OR EXISTS(SELECT 1 FROM attendance_events)
                OR EXISTS(SELECT 1 FROM capture_challenges) OR EXISTS(SELECT 1 FROM attendance_operations) THEN
                RAISE EXCEPTION 'Cannot downgrade attendance recording while retained records exist';
            END IF;
        END $$;
        """;

    private const string RecordingGuards = """
        CREATE FUNCTION attendance_recording_write_lock() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'Attendance writes require read committed isolation' USING ERRCODE='25001';
            END IF;
            PERFORM pg_advisory_xact_lock(7241002);
            RETURN NULL;
        END $$;
        CREATE FUNCTION attendance_recording_no_remove() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Attendance recording history cannot be removed' USING ERRCODE='23514';
        END $$;
        DO $$ DECLARE tab text; BEGIN
            FOREACH tab IN ARRAY ARRAY['attendance_days','attendance_events','capture_challenges','attendance_operations'] LOOP
                EXECUTE format('CREATE TRIGGER attendance_recording_lock BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH STATEMENT EXECUTE FUNCTION attendance_recording_write_lock()',tab);
                EXECUTE format('CREATE TRIGGER attendance_recording_no_delete BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION attendance_recording_no_remove()',tab);
                EXECUTE format('CREATE TRIGGER attendance_recording_no_truncate BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION attendance_recording_no_remove()',tab);
            END LOOP;
        END $$;

        CREATE FUNCTION attendance_preserve_day() RETURNS trigger LANGUAGE plpgsql AS $$
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
        CREATE TRIGGER attendance_day_guard BEFORE INSERT OR UPDATE ON attendance_days FOR EACH ROW EXECUTE FUNCTION attendance_preserve_day();

        CREATE FUNCTION attendance_preserve_event() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE entered_at timestamptz;
        BEGIN
            IF TG_OP='UPDATE' THEN
                RAISE EXCEPTION 'Original attendance events are immutable' USING ERRCODE='23514';
            END IF;
            SELECT snapshot_at_utc INTO entered_at FROM attendance_days WHERE id=NEW.day_id AND employee_id=NEW.actor_id;
            -- Missing/wrong owners are rejected by the composite FK instead.
            IF FOUND AND ((NEW.action='CheckIn' AND NEW.occurred_at_utc<>entered_at)
                OR (NEW.action='CheckOut' AND (NEW.occurred_at_utc<=entered_at
                    OR NOT EXISTS(SELECT 1 FROM attendance_events WHERE day_id=NEW.day_id AND action='CheckIn')))) THEN
                RAISE EXCEPTION 'Attendance event must follow original check-in' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER attendance_event_guard BEFORE INSERT OR UPDATE ON attendance_events FOR EACH ROW EXECUTE FUNCTION attendance_preserve_event();

        CREATE FUNCTION attendance_preserve_challenge() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW.id,NEW.actor_id,NEW.session_id,NEW.action,NEW.workspace_id,NEW.project_id,NEW.site_id,NEW.idempotency_key,NEW.issued_at_utc,NEW.expires_at_utc)
                IS DISTINCT FROM ROW(OLD.id,OLD.actor_id,OLD.session_id,OLD.action,OLD.workspace_id,OLD.project_id,OLD.site_id,OLD.idempotency_key,OLD.issued_at_utc,OLD.expires_at_utc)
                OR (OLD.consumed_operation_id IS NOT NULL AND NEW.consumed_operation_id IS DISTINCT FROM OLD.consumed_operation_id) THEN
                RAISE EXCEPTION 'Capture challenge identity and consumption are immutable' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER attendance_challenge_guard BEFORE UPDATE ON capture_challenges FOR EACH ROW EXECUTE FUNCTION attendance_preserve_challenge();

        CREATE FUNCTION attendance_preserve_operation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.challenge_id,NEW.actor_id,NEW.session_id,NEW.action,NEW.workspace_id,NEW.project_id,NEW.site_id,NEW.idempotency_key,
                    NEW.payload_sha256,NEW.occurred_at_utc,NEW.membership_id,NEW.membership_workspace_id,NEW.department_id,NEW.target_day_id,NEW.expected_day_version)
                    IS DISTINCT FROM ROW(OLD.id,OLD.challenge_id,OLD.actor_id,OLD.session_id,OLD.action,OLD.workspace_id,OLD.project_id,OLD.site_id,OLD.idempotency_key,
                    OLD.payload_sha256,OLD.occurred_at_utc,OLD.membership_id,OLD.membership_workspace_id,OLD.department_id,OLD.target_day_id,OLD.expected_day_version)
                    OR (NEW IS DISTINCT FROM OLD AND (NEW.version<=OLD.version OR NEW.fencing_version<OLD.fencing_version)) THEN
                    RAISE EXCEPTION 'Attendance operation pin is immutable and versions must advance' USING ERRCODE='23514';
                END IF;
                IF OLD.state<>'Processing' AND NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'Terminal attendance operation is immutable' USING ERRCODE='23514';
                END IF;
            ELSIF NEW.state<>'Processing' THEN
                RAISE EXCEPTION 'Attendance operation must start processing' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER attendance_operation_guard BEFORE INSERT OR UPDATE ON attendance_operations FOR EACH ROW EXECUTE FUNCTION attendance_preserve_operation();
        """;

    private const string DropRecordingGuards = """
        DROP FUNCTION attendance_preserve_operation();
        DROP FUNCTION attendance_preserve_challenge();
        DROP FUNCTION attendance_preserve_event();
        DROP FUNCTION attendance_preserve_day();
        DROP FUNCTION attendance_recording_no_remove();
        DROP FUNCTION attendance_recording_write_lock();
        """;
}
