namespace TPR10.Api.Data.Migrations;

public partial class AddFieldAdvanceRequests
{
    // Frozen SQL, as with 7A. No dependency on evolving application guard code.
    private const string RequestGuards = """
        CREATE FUNCTION advance_request_reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Request history is immutable' USING ERRCODE='23514';
        END $$;

        CREATE FUNCTION advance_request_guard_identity() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Request history must be retained' USING ERRCODE='23514';
            END IF;
            IF ROW(NEW.id,NEW.workspace_id,NEW.project_id,NEW.owner_person_id,NEW.delegate_person_id,
                   NEW.source_request_id,NEW.created_by,NEW.created_at_utc)
                IS DISTINCT FROM
               ROW(OLD.id,OLD.workspace_id,OLD.project_id,OLD.owner_person_id,OLD.delegate_person_id,
                   OLD.source_request_id,OLD.created_by,OLD.created_at_utc) THEN
                RAISE EXCEPTION 'Request identity is immutable' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_request_identity BEFORE UPDATE OR DELETE ON field_advance_requests
            FOR EACH ROW EXECUTE FUNCTION advance_request_guard_identity();

        CREATE FUNCTION advance_request_guard_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Revision history must be retained' USING ERRCODE='23514';
            END IF;
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.workspace_id,NEW.project_id,NEW.request_id,NEW.revision_number,
                       NEW.created_by,NEW.created_by_person_id,NEW.created_at_utc)
                    IS DISTINCT FROM
                   ROW(OLD.id,OLD.workspace_id,OLD.project_id,OLD.request_id,OLD.revision_number,
                       OLD.created_by,OLD.created_by_person_id,OLD.created_at_utc) THEN
                    RAISE EXCEPTION 'Revision identity is immutable' USING ERRCODE='23514';
                END IF;
                IF OLD.submitted_at_utc IS NOT NULL AND
                    (to_jsonb(NEW)-'is_current') IS DISTINCT FROM (to_jsonb(OLD)-'is_current') THEN
                    RAISE EXCEPTION 'Submitted revision is immutable' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_request_revision BEFORE INSERT OR UPDATE OR DELETE ON field_advance_request_revisions
            FOR EACH ROW EXECUTE FUNCTION advance_request_guard_revision();

        CREATE FUNCTION advance_request_guard_child() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            target_revision uuid;
            submitted timestamptz;
        BEGIN
            target_revision := CASE WHEN TG_OP='DELETE' THEN OLD.revision_id ELSE NEW.revision_id END;
            IF TG_OP='UPDATE' AND ROW(NEW.id,NEW.workspace_id,NEW.project_id,NEW.request_id,NEW.revision_id)
                IS DISTINCT FROM ROW(OLD.id,OLD.workspace_id,OLD.project_id,OLD.request_id,OLD.revision_id) THEN
                RAISE EXCEPTION 'Revision child identity is immutable' USING ERRCODE='23514';
            END IF;
            -- A write, not only a row lock: stale repeatable-read transactions must fail
            -- rather than checking a pre-submission snapshot after waiting for submit.
            UPDATE field_advance_request_revisions SET is_current=is_current WHERE id=target_revision
                RETURNING submitted_at_utc INTO submitted;
            IF TG_TABLE_NAME='field_advance_request_lines' THEN
                IF submitted IS NOT NULL THEN
                    RAISE EXCEPTION 'Submitted lines are immutable' USING ERRCODE='23514';
                END IF;
            ELSE
                IF TG_OP='UPDATE' AND OLD.outcome IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'Approval result is immutable' USING ERRCODE='23514';
                END IF;
                IF TG_OP='DELETE' AND OLD.outcome IS NOT NULL THEN
                    RAISE EXCEPTION 'Approval result must be retained' USING ERRCODE='23514';
                END IF;
                IF submitted IS NOT NULL THEN
                    IF TG_OP IN ('INSERT','DELETE') THEN
                        RAISE EXCEPTION 'Submitted approval route is immutable' USING ERRCODE='23514';
                    END IF;
                    IF ROW(NEW.step_order,NEW.level_code) IS DISTINCT FROM ROW(OLD.step_order,OLD.level_code) THEN
                        RAISE EXCEPTION 'Submitted approval role is immutable' USING ERRCODE='23514';
                    END IF;
                END IF;
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_request_line BEFORE INSERT OR UPDATE OR DELETE ON field_advance_request_lines
            FOR EACH ROW EXECUTE FUNCTION advance_request_guard_child();
        CREATE TRIGGER advance_request_step BEFORE INSERT OR UPDATE OR DELETE ON field_advance_approval_steps
            FOR EACH ROW EXECUTE FUNCTION advance_request_guard_child();
        CREATE TRIGGER advance_request_event BEFORE UPDATE OR DELETE ON field_advance_request_events
            FOR EACH ROW EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_request_participant BEFORE UPDATE OR DELETE ON field_advance_request_participants
            FOR EACH ROW EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_request_truncate BEFORE TRUNCATE ON field_advance_requests
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_revision_truncate BEFORE TRUNCATE ON field_advance_request_revisions
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_line_truncate BEFORE TRUNCATE ON field_advance_request_lines
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_step_truncate BEFORE TRUNCATE ON field_advance_approval_steps
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_event_truncate BEFORE TRUNCATE ON field_advance_request_events
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER advance_participant_truncate BEFORE TRUNCATE ON field_advance_request_participants
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        """;

    private const string RemoveRequestGuards = """
        LOCK TABLE field_advance_requests, field_advance_request_revisions, field_advance_request_lines,
            field_advance_approval_steps, field_advance_request_events, field_advance_request_participants IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM field_advance_requests) THEN
                RAISE EXCEPTION 'Cannot discard recorded requests' USING ERRCODE='23514';
            END IF;
        END $$;
        DROP FUNCTION advance_request_guard_child() CASCADE;
        DROP FUNCTION advance_request_guard_revision() CASCADE;
        DROP FUNCTION advance_request_guard_identity() CASCADE;
        DROP FUNCTION advance_request_reject_history_mutation() CASCADE;
        """;
}
