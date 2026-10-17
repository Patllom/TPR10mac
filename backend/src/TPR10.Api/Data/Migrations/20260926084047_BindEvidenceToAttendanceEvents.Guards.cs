namespace TPR10.Api.Data.Migrations;

// Frozen SQL: migration preflight never invents attendance history for legacy bindings.
public partial class BindEvidenceToAttendanceEvents
{
    private const string BindingUpPreflight = """
        LOCK TABLE evidence_bindings, evidence_objects, attendance_events, attendance_days IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM evidence_bindings b
                LEFT JOIN evidence_objects o ON o.id=b.evidence_id
                LEFT JOIN attendance_events e ON e.id=b.event_id
                LEFT JOIN attendance_days d ON d.id=e.day_id
                WHERE e.id IS NULL OR d.id IS NULL OR o.id IS NULL
                    OR ROW(e.evidence_id,e.actor_id,e.action,e.occurred_at_utc,d.employee_id)
                        IS DISTINCT FROM ROW(o.id,o.owner_id,o.action,o.occurred_at_utc,o.owner_id)
            ) THEN
                RAISE EXCEPTION 'Evidence binding preflight: retained binding has no matching attendance event';
            END IF;
        END $$;
        """;

    private const string BindingGuards = """
        ALTER TABLE evidence_bindings ALTER CONSTRAINT fk_evidence_binding_attendance_event DEFERRABLE INITIALLY DEFERRED;
        CREATE FUNCTION evidence_validate_attendance_binding() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM evidence_bindings b
                JOIN evidence_objects o ON o.id=b.evidence_id
                JOIN attendance_events e ON e.id=b.event_id
                JOIN attendance_days d ON d.id=e.day_id
                WHERE b.evidence_id=NEW.evidence_id
                    AND e.evidence_id=o.id AND e.actor_id=o.owner_id
                    AND e.action=o.action AND e.occurred_at_utc=o.occurred_at_utc
                    AND d.employee_id=o.owner_id
            ) THEN
                RAISE EXCEPTION 'Evidence binding does not match attendance event' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER evidence_attendance_consistency AFTER INSERT OR UPDATE ON evidence_bindings
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_validate_attendance_binding();
        """;

    private const string BindingDownPreflight = """
        LOCK TABLE evidence_bindings, attendance_events IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM evidence_bindings) THEN
                RAISE EXCEPTION 'Cannot downgrade evidence binding protection while retained bindings exist';
            END IF;
        END $$;
        """;
}
