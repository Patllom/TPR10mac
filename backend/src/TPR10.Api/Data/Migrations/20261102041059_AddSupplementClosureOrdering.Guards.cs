namespace TPR10.Api.Data.Migrations;

public partial class AddSupplementClosureOrdering
{
    // No DDL before this check; locks and the backfill live in the migration transaction.
    private const string LegacyPreflight = """
        DO $$ BEGIN PERFORM pg_advisory_xact_lock(7241002); END $$;
        LOCK TABLE field_advance_families,field_advance_requests,field_advance_request_revisions,
            field_advance_request_events,field_advance_command_receipts,field_advance_closure_records IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM field_advance_requests s
                LEFT JOIN field_advance_requests r ON r.id=s.supplement_of_request_id
                LEFT JOIN field_advance_families f ON f.id=s.family_id
                WHERE s.relation_kind='Supplement' AND (
                    r.id IS NULL OR r.relation_kind<>'Normal' OR r.state='Closed'
                    OR ROW(r.workspace_id,r.project_id,r.family_id,r.owner_person_id)
                        IS DISTINCT FROM ROW(s.workspace_id,s.project_id,s.family_id,s.owner_person_id)
                    OR f.id IS NULL OR f.version<=0
                    OR ROW(f.workspace_id,f.project_id,f.owner_person_id)
                        IS DISTINCT FROM ROW(s.workspace_id,s.project_id,s.owner_person_id)
                    OR EXISTS(SELECT 1 FROM field_advance_closure_records c WHERE c.request_id=r.id)
                    OR (SELECT count(*) FROM field_advance_request_events e WHERE e.request_id=s.id AND e.kind='SupplementCreated')<>1
                    OR NOT EXISTS (
                        SELECT 1 FROM field_advance_request_events e
                        JOIN field_advance_command_receipts c ON c.command_id=e.command_id
                        JOIN field_advance_request_revisions v ON v.id=e.revision_id
                        WHERE e.request_id=s.id AND e.kind='SupplementCreated' AND e.aggregate_version=1
                            AND e.workspace_id=s.workspace_id AND e.project_id=s.project_id
                            AND e.actor_id=s.created_by AND c.actor_id=e.actor_id AND c.workspace_id=s.workspace_id
                            AND c.action='supplement.create' AND c.status=201
                            AND c.result_json->>'Status'='201' AND c.result_json->'Value'->>'Id'=s.id::text
                            AND c.result_json->'Value'->>'RevisionId'=v.id::text
                            AND v.request_id=s.id AND v.workspace_id=s.workspace_id AND v.project_id=s.project_id
                            AND v.revision_number=1 AND v.created_by=e.actor_id AND v.created_by_person_id=e.actor_person_id
                    )
                )
            ) THEN RAISE EXCEPTION 'supplement-order-legacy-unproven' USING ERRCODE='23514'; END IF;
        END $$;
        """;

    private const string OrderGuards = """
        CREATE FUNCTION supplement_order_proof() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE root_state text;
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.family_version_at_creation,NEW.legacy_family_version_baseline)
                    IS DISTINCT FROM ROW(OLD.family_version_at_creation,OLD.legacy_family_version_baseline)
                THEN RAISE EXCEPTION 'supplement-order-immutable' USING ERRCODE='23514'; END IF;
                RETURN NEW;
            END IF;
            IF NEW.relation_kind='Normal' THEN
                IF NEW.family_version_at_creation IS NOT NULL OR NEW.legacy_family_version_baseline IS NOT NULL
                THEN RAISE EXCEPTION 'supplement-order-invalid' USING ERRCODE='23514'; END IF;
                RETURN NEW;
            END IF;
            PERFORM pg_advisory_xact_lock(7241002);
            PERFORM 1 FROM field_advance_families WHERE id=NEW.family_id FOR UPDATE;
            SELECT state INTO root_state FROM field_advance_requests r WHERE r.id=NEW.supplement_of_request_id
                AND r.relation_kind='Normal' AND r.family_id=NEW.family_id AND r.workspace_id=NEW.workspace_id
                AND r.project_id=NEW.project_id AND r.owner_person_id=NEW.owner_person_id;
            IF root_state IS NULL OR root_state IN ('Closed','Rejected','Withdrawn')
                OR EXISTS(SELECT 1 FROM field_advance_closure_records WHERE request_id=NEW.supplement_of_request_id)
                OR NEW.family_version_at_creation IS NULL OR NEW.family_version_at_creation<=0
                OR NEW.legacy_family_version_baseline IS NOT NULL
            THEN RAISE EXCEPTION 'supplement-order-create-denied' USING ERRCODE='23514'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER supplement_order_proof BEFORE INSERT OR UPDATE ON field_advance_requests
            FOR EACH ROW EXECUTE FUNCTION supplement_order_proof();

        CREATE FUNCTION supplement_order_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.relation_kind='Supplement' AND NEW.family_version_at_creation IS DISTINCT FROM
                (SELECT version FROM field_advance_families WHERE id=NEW.family_id)
            THEN RAISE EXCEPTION 'supplement-order-commit-mismatch' USING ERRCODE='23514'; END IF;
            RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER supplement_order_commit AFTER INSERT ON field_advance_requests
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION supplement_order_commit();

        CREATE FUNCTION supplement_closure_order() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM pg_advisory_xact_lock(7241002);
            PERFORM 1 FROM field_advance_families WHERE id=NEW.family_id FOR UPDATE;
            IF EXISTS(SELECT 1 FROM field_advance_requests s WHERE s.supplement_of_request_id=NEW.request_id
                AND (COALESCE(s.family_version_at_creation,s.legacy_family_version_baseline) IS NULL
                    OR COALESCE(s.family_version_at_creation,s.legacy_family_version_baseline)>=NEW.family_version))
            THEN RAISE EXCEPTION 'supplement-order-closure-not-later' USING ERRCODE='23514'; END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER supplement_closure_order BEFORE INSERT ON field_advance_closure_records
            FOR EACH ROW EXECUTE FUNCTION supplement_closure_order();
        CREATE FUNCTION supplement_closure_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS(SELECT 1 FROM field_advance_requests WHERE supplement_of_request_id=NEW.request_id)
                AND NEW.family_version IS DISTINCT FROM (SELECT version FROM field_advance_families WHERE id=NEW.family_id)
            THEN RAISE EXCEPTION 'supplement-order-closure-commit-mismatch' USING ERRCODE='23514'; END IF;
            RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER supplement_closure_commit AFTER INSERT ON field_advance_closure_records
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION supplement_closure_commit();
        """;
}
