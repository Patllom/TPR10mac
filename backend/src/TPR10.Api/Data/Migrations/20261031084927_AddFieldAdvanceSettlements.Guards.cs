namespace TPR10.Api.Data.Migrations;

public partial class AddFieldAdvanceSettlements
{
    private const string SettlementGuards = """
        CREATE FUNCTION advance_settlement_guard_accepted() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.accepted_at_utc IS NOT NULL THEN
                RAISE EXCEPTION 'Accepted settlement history is immutable' USING ERRCODE='23514';
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION advance_settlement_guard_revision_child() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE parent_id uuid; accepted timestamptz;
        BEGIN
            -- Lock both parents on a move, including the OLD parent, so moving a child
            -- cannot bypass the accepted snapshot. Commands still use the family lock.
            FOR parent_id IN SELECT DISTINCT id FROM unnest(ARRAY[
                CASE WHEN TG_OP<>'INSERT' THEN OLD.revision_id END,
                CASE WHEN TG_OP<>'DELETE' THEN NEW.revision_id END]) id WHERE id IS NOT NULL ORDER BY id
            LOOP
                SELECT accepted_at_utc INTO accepted FROM field_advance_settlement_revisions WHERE id=parent_id FOR UPDATE;
                IF accepted IS NOT NULL THEN
                    RAISE EXCEPTION 'Accepted revision contents are immutable' USING ERRCODE='23514';
                END IF;
            END LOOP;
            IF TG_OP='DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END $$;

        CREATE FUNCTION advance_settlement_guard_document() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- Document mutation already owns the document row. Serialize on referencing
            -- revisions in GUID order: document -> revision, never revision -> document.
            -- Independent allocation acceptance also locks its revision in the child guard.
            -- Do NOT lock allocation rows here: their writes lock allocation -> revision.
            PERFORM r.id FROM field_advance_settlement_revisions r
                WHERE r.id IN (SELECT a.revision_id FROM field_advance_expense_allocations a WHERE a.document_id=OLD.id)
                ORDER BY r.id FOR UPDATE;
            -- Separate statement obtains a fresh READ COMMITTED snapshot after any wait.
            IF EXISTS (SELECT 1 FROM field_advance_expense_allocations a
                JOIN field_advance_settlement_revisions r ON r.id=a.revision_id
                WHERE a.document_id=OLD.id AND (a.accepted_at_utc IS NOT NULL OR r.accepted_at_utc IS NOT NULL)) THEN
                RAISE EXCEPTION 'Evidence used in accepted settlement must be retained' USING ERRCODE='23514';
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER settlement_document_history BEFORE UPDATE OR DELETE ON field_advance_expense_documents
            FOR EACH ROW EXECUTE FUNCTION advance_settlement_guard_document();

        DO $$ DECLARE t text; BEGIN
            FOREACH t IN ARRAY ARRAY['settlement_rounds','settlement_revisions','settlement_lines','settlement_approval_steps','expense_documents','expense_allocations','expense_duplicate_cases','work_completions','submission_declarations','refunds','refund_corrections','settlement_expense_adjustments','settlement_adjustment_cases','payment_incident_resolutions','payment_incident_recoveries','closure_records'] LOOP
                EXECUTE format('CREATE TRIGGER settlement_accepted_history BEFORE UPDATE OR DELETE ON field_advance_%I FOR EACH ROW EXECUTE FUNCTION advance_settlement_guard_accepted()',t);
                EXECUTE format('CREATE TRIGGER settlement_no_truncate BEFORE TRUNCATE ON field_advance_%I FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation()',t);
            END LOOP;
            FOREACH t IN ARRAY ARRAY['settlement_lines','settlement_approval_steps','expense_allocations'] LOOP
                EXECUTE format('CREATE TRIGGER settlement_revision_contents BEFORE INSERT OR UPDATE OR DELETE ON field_advance_%I FOR EACH ROW EXECUTE FUNCTION advance_settlement_guard_revision_child()',t);
            END LOOP;
        END $$;
        """;

    private const string SettlementDowngradeGuard = """
        DO $$ DECLARE t text; populated boolean; BEGIN
            FOREACH t IN ARRAY ARRAY['settlement_rounds','settlement_revisions','settlement_lines','settlement_approval_steps','expense_documents','expense_allocations','expense_duplicate_cases','work_completions','submission_declarations','refunds','refund_corrections','settlement_expense_adjustments','settlement_adjustment_cases','payment_incident_resolutions','payment_incident_recoveries','closure_records'] LOOP
                -- Hold the same table lock needed by DROP through the migration transaction.
                -- Otherwise a writer can insert after the empty check and before DROP.
                EXECUTE format('LOCK TABLE field_advance_%I IN ACCESS EXCLUSIVE MODE',t);
                EXECUTE format('SELECT EXISTS (SELECT 1 FROM field_advance_%I)',t) INTO populated;
                IF populated THEN RAISE EXCEPTION 'Settlement history prevents destructive downgrade' USING ERRCODE='23514'; END IF;
            END LOOP;
        END $$;
        """;
}
