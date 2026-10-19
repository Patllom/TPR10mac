namespace TPR10.Api.Data.Migrations;

public partial class AddFieldAdvancePolicyFoundation
{
    // Frozen migration SQL: provenance survives later edits and account-link changes.
    private const string HistoryGuards = """
        CREATE FUNCTION field_advance_reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'ประวัติผู้ร่วมแก้ไขต้องเก็บไว้' USING ERRCODE='23514';
        END $$;
        CREATE TRIGGER field_advance_contributor_history
            BEFORE UPDATE OR DELETE ON field_advance_policy_contributors
            FOR EACH ROW EXECUTE FUNCTION field_advance_reject_history_mutation();
        CREATE TRIGGER field_advance_contributor_truncate
            BEFORE TRUNCATE ON field_advance_policy_contributors
            FOR EACH STATEMENT EXECUTE FUNCTION field_advance_reject_history_mutation();

        CREATE FUNCTION finance_guard_account_link() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP IN ('DELETE','TRUNCATE') THEN
                RAISE EXCEPTION 'ต้องเก็บประวัติการเชื่อมบัญชี' USING ERRCODE='23514';
            END IF;
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.workspace_id,NEW.user_id,NEW.person_id,NEW.created_by,NEW.created_at_utc,NEW.reason)
                    IS DISTINCT FROM ROW(OLD.id,OLD.workspace_id,OLD.user_id,OLD.person_id,OLD.created_by,OLD.created_at_utc,OLD.reason)
                    OR OLD.ended_at_utc IS NOT NULL OR NEW.ended_at_utc IS NULL OR NEW.version<>OLD.version+1 THEN
                    RAISE EXCEPTION 'แก้ตัวตนย้อนหลังไม่ได้ ให้สิ้นสุดการเชื่อมบัญชีเดิม' USING ERRCODE='23514';
                END IF;
            END IF;
            -- A row lock alone cannot refresh a REPEATABLE READ snapshot. A no-op write
            -- creates a write/write conflict: stale transactions must retry after 40001.
            -- Keep the security version unchanged; this is not account/session revocation.
            UPDATE users SET security_version=security_version WHERE id=NEW.user_id;
            IF EXISTS (
                SELECT 1 FROM finance_account_links l WHERE l.workspace_id=NEW.workspace_id
                  AND l.user_id=NEW.user_id AND l.id<>NEW.id
                  AND tstzrange(l.created_at_utc,l.ended_at_utc,'[)') && tstzrange(NEW.created_at_utc,NEW.ended_at_utc,'[)')
            ) THEN
                RAISE EXCEPTION 'ช่วงเวลาการเชื่อมบัญชีซ้ำกัน' USING ERRCODE='23505';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER finance_account_link_history BEFORE INSERT OR UPDATE OR DELETE ON finance_account_links
            FOR EACH ROW EXECUTE FUNCTION finance_guard_account_link();
        CREATE TRIGGER finance_account_link_truncate BEFORE TRUNCATE ON finance_account_links
            FOR EACH STATEMENT EXECUTE FUNCTION finance_guard_account_link();
        """;

    private const string DropHistoryGuards = """
        DROP FUNCTION field_advance_reject_history_mutation() CASCADE;
        DROP FUNCTION finance_guard_account_link() CASCADE;
        """;

    private const string AssertEmptyFoundation = """
        LOCK TABLE finance_persons, finance_account_links, field_advance_policy_revisions,
            field_advance_approval_bands, field_advance_category_rules, field_advance_policy_contributors,
            field_advance_policy_reviews, field_advance_active_policies, field_advance_grant_proposals,
            field_advance_grant_reviews, field_advance_grant_assignments, field_advance_command_receipts,
            field_advance_bootstrap_receipts IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM finance_persons) OR EXISTS (SELECT 1 FROM field_advance_command_receipts)
                OR EXISTS (SELECT 1 FROM field_advance_policy_revisions) OR EXISTS (SELECT 1 FROM field_advance_grant_proposals) THEN
                RAISE EXCEPTION 'ไม่สามารถย้อน migration ที่มีประวัติการเงินแล้ว' USING ERRCODE='23514';
            END IF;
        END $$;
        """;
}
