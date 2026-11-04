namespace TPR10.Api.Data.Migrations;

public partial class AddIncidentPaymentBridge
{
    private const string BridgeGuards = """
        CREATE TRIGGER incident_adjustment_no_truncate BEFORE TRUNCATE ON field_advance_incident_payment_adjustments
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE TRIGGER incident_claim_no_truncate BEFORE TRUNCATE ON field_advance_payment_shortfall_claims
            FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation();
        CREATE FUNCTION advance_incident_adjustment_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Incident reservation adjustment is immutable' USING ERRCODE='23514'; END $$;
        CREATE TRIGGER incident_adjustment_immutable BEFORE UPDATE OR DELETE ON field_advance_incident_payment_adjustments
            FOR EACH ROW EXECUTE FUNCTION advance_incident_adjustment_immutable();

        CREATE FUNCTION advance_incident_claim_history() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' OR OLD.state<>'Reserved'
                OR (to_jsonb(OLD)-'amount'-'state') IS DISTINCT FROM (to_jsonb(NEW)-'amount'-'state') THEN
                RAISE EXCEPTION 'Shortfall claim history is immutable' USING ERRCODE='23514';
            END IF;
            IF NEW.state<>'Reserved' AND NEW.amount<>OLD.amount THEN
                RAISE EXCEPTION 'Final claim amount cannot change' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER incident_claim_history BEFORE UPDATE OR DELETE ON field_advance_payment_shortfall_claims
            FOR EACH ROW EXECUTE FUNCTION advance_incident_claim_history();

        -- Deferred because the account, claim, revision and ledger are persisted together.
        -- Direct SQL cannot invent activation without matching scoped provenance.
        CREATE FUNCTION advance_incident_capacity() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE target uuid; account_record record;
        BEGIN
            target := NEW.request_id;
            SELECT * INTO account_record FROM field_advance_payment_accounts WHERE request_id=target FOR UPDATE;
            IF NOT FOUND THEN RETURN NULL; END IF;
            IF account_record.incident_reservation_usage <> COALESCE((SELECT sum(amount) FROM field_advance_incident_payment_adjustments WHERE request_id=target),0)
                OR account_record.activated_shortfall <> COALESCE((SELECT sum(amount) FROM field_advance_payment_shortfall_claims WHERE request_id=target AND state<>'Cancelled'),0) THEN
                RAISE EXCEPTION 'Incident capacity projection lacks provenance' USING ERRCODE='23514';
            END IF;
            IF EXISTS (
                SELECT 1 FROM field_advance_incident_payment_adjustments a
                JOIN field_advance_payment_incident_resolutions i ON i.id=a.incident_id
                JOIN field_advance_payment_holds h ON h.id=i.hold_id
                WHERE a.request_id=target AND (i.accepted_at_utc IS NULL OR h.released_at_utc IS NULL
                    OR a.amount<>COALESCE((SELECT sum(CASE WHEN e.action='Reserve' THEN e.amount ELSE -e.amount END)
                        FROM field_advance_reservation_entries e WHERE e.installment_id=a.installment_id),0))) THEN
                RAISE EXCEPTION 'Incident source reservation is not accounted once' USING ERRCODE='23514';
            END IF;
            IF EXISTS (
                SELECT 1 FROM field_advance_payment_shortfall_claims c
                JOIN field_advance_payment_incident_resolutions i ON i.id=c.incident_id
                JOIN field_advance_payment_attempts a ON a.id=c.source_attempt_id
                JOIN field_advance_installment_revisions v ON v.id=a.revision_id
                WHERE c.request_id=target AND (i.accepted_at_utc IS NULL
                    OR NOT EXISTS (SELECT 1 FROM field_advance_incident_payment_adjustments x WHERE x.incident_id=i.id)
                    OR (SELECT COALESCE(sum(amount),0) FROM field_advance_payment_shortfall_claims x WHERE x.incident_id=i.id AND x.state<>'Cancelled')>greatest(0,v.amount-i.owner_received)
                    OR (c.state='Reserved' AND (c.amount<>COALESCE((SELECT sum(CASE WHEN e.action='Reserve' THEN e.amount ELSE -e.amount END)
                        FROM field_advance_reservation_entries e WHERE e.installment_id=c.installment_id),0)
                        OR NOT EXISTS (SELECT 1 FROM field_advance_installment_revisions r WHERE r.installment_id=c.installment_id AND r.is_current AND r.amount=c.amount)))
                    OR (c.state='Consumed' AND NOT EXISTS (SELECT 1 FROM field_advance_reservation_entries e WHERE e.installment_id=c.installment_id AND e.action='Consume' AND e.amount=c.amount))
                    OR (c.state='Settled' AND NOT EXISTS (SELECT 1 FROM field_advance_incident_payment_adjustments x WHERE x.installment_id=c.installment_id AND x.amount=c.amount))
                    OR (c.state='Cancelled' AND COALESCE((SELECT sum(CASE WHEN e.action='Reserve' THEN e.amount ELSE -e.amount END)
                        FROM field_advance_reservation_entries e WHERE e.installment_id=c.installment_id),0)<>0))) THEN
                RAISE EXCEPTION 'Shortfall claim exceeds its scoped entitlement or reservation' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER incident_capacity_account AFTER INSERT OR UPDATE ON field_advance_payment_accounts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION advance_incident_capacity();
        CREATE CONSTRAINT TRIGGER incident_capacity_adjustment AFTER INSERT ON field_advance_incident_payment_adjustments
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION advance_incident_capacity();
        CREATE CONSTRAINT TRIGGER incident_capacity_claim AFTER INSERT OR UPDATE ON field_advance_payment_shortfall_claims
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION advance_incident_capacity();
        """;
}
