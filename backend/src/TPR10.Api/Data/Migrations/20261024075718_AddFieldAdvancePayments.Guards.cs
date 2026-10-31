namespace TPR10.Api.Data.Migrations;

public partial class AddFieldAdvancePayments
{
    private const string BackfillPayments = """
        -- The migration holds DDL locks; only the submitted-row guard is suspended for this one backfill.
        ALTER TABLE field_advance_request_revisions DISABLE TRIGGER advance_request_revision;
        UPDATE field_advance_request_revisions SET approval_basis_amount=total,
            approval_basis_json=jsonb_build_object('Amount',total,'Items',jsonb_build_array(
                jsonb_build_object('RequestId',request_id,'RevisionId',id,'ApprovedAmount',total)));
        ALTER TABLE field_advance_request_revisions ENABLE TRIGGER advance_request_revision;

        INSERT INTO field_advance_families(id,workspace_id,project_id,owner_person_id,version)
            SELECT family_id,workspace_id,project_id,owner_person_id,1 FROM field_advance_requests;
        INSERT INTO field_advance_payment_accounts(id,workspace_id,project_id,family_id,request_id,
            approved_revision_id,approved,spendable,reserved,paid,ever_paid,version)
        SELECT gen_random_uuid(),r.workspace_id,r.project_id,r.family_id,r.id,a.id,
            COALESCE(a.total,0),COALESCE(a.total,0),0,0,false,1
        FROM field_advance_requests r LEFT JOIN LATERAL (
            SELECT v.id,v.total FROM field_advance_request_revisions v WHERE v.request_id=r.id AND v.total>0
            AND ((r.state='Approved' AND v.is_current) OR
                (v.submitted_at_utc IS NOT NULL
                 AND EXISTS (SELECT 1 FROM field_advance_approval_steps s WHERE s.revision_id=v.id)
                 AND NOT EXISTS (SELECT 1 FROM field_advance_approval_steps s WHERE s.revision_id=v.id AND s.outcome IS DISTINCT FROM 'Approved')))
            ORDER BY v.revision_number DESC LIMIT 1
        ) a ON true;
        -- Retain historical roles without treating them as new payments or reservations.
        INSERT INTO field_advance_payment_participations(id,workspace_id,project_id,family_id,person_id,role,created_by,created_at_utc)
        SELECT gen_random_uuid(),p.workspace_id,p.project_id,r.family_id,p.person_id,p.role,p.created_by,p.created_at_utc
        FROM field_advance_request_participants p JOIN field_advance_requests r ON r.id=p.request_id;
        """;

    private const string PaymentGuards = """
        CREATE FUNCTION advance_payment_initialize_request() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.relation_kind='Normal' THEN
                INSERT INTO field_advance_families(id,workspace_id,project_id,owner_person_id,version)
                    VALUES(NEW.family_id,NEW.workspace_id,NEW.project_id,NEW.owner_person_id,1)
                    ON CONFLICT (id) DO NOTHING;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_initialize_request BEFORE INSERT ON field_advance_requests
            FOR EACH ROW EXECUTE FUNCTION advance_payment_initialize_request();

        CREATE FUNCTION advance_payment_initialize_account() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            INSERT INTO field_advance_payment_accounts(id,workspace_id,project_id,family_id,request_id,
                approved,spendable,reserved,paid,ever_paid,version)
                VALUES(gen_random_uuid(),NEW.workspace_id,NEW.project_id,NEW.family_id,NEW.id,0,0,0,0,false,1);
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_initialize_account AFTER INSERT ON field_advance_requests
            FOR EACH ROW EXECUTE FUNCTION advance_payment_initialize_account();

        CREATE FUNCTION advance_payment_normal_basis() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS (SELECT 1 FROM field_advance_requests WHERE id=NEW.request_id AND relation_kind='Normal') THEN
                NEW.approval_basis_amount := NEW.total;
                NEW.approval_basis_json := jsonb_build_object('Amount',NEW.total,'Items',jsonb_build_array(
                    jsonb_build_object('RequestId',NEW.request_id,'RevisionId',NEW.id,'ApprovedAmount',NEW.total)));
            END IF;
            RETURN NEW;
        END $$;
        -- Runs before the existing immutable submitted-revision guard (alphabetical trigger ordering).
        CREATE TRIGGER advance_payment_normal_basis BEFORE INSERT OR UPDATE ON field_advance_request_revisions
            FOR EACH ROW EXECUTE FUNCTION advance_payment_normal_basis();

        CREATE FUNCTION advance_payment_guard_request_family() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW.family_id,NEW.relation_kind,NEW.supplement_of_request_id)
                IS DISTINCT FROM ROW(OLD.family_id,OLD.relation_kind,OLD.supplement_of_request_id) THEN
                RAISE EXCEPTION 'Request family identity is immutable' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_request_family BEFORE UPDATE ON field_advance_requests
            FOR EACH ROW EXECUTE FUNCTION advance_payment_guard_request_family();

        CREATE FUNCTION advance_payment_guard_account() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Payment account must be retained' USING ERRCODE='23514';
            END IF;
            IF ROW(NEW.id,NEW.workspace_id,NEW.project_id,NEW.family_id,NEW.request_id)
                IS DISTINCT FROM ROW(OLD.id,OLD.workspace_id,OLD.project_id,OLD.family_id,OLD.request_id)
                OR (OLD.ever_paid AND NOT NEW.ever_paid) THEN
                RAISE EXCEPTION 'Payment account identity and paid history must be retained' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_account BEFORE UPDATE OR DELETE ON field_advance_payment_accounts
            FOR EACH ROW EXECUTE FUNCTION advance_payment_guard_account();

        CREATE FUNCTION advance_payment_guard_family() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Family history must be retained' USING ERRCODE='23514';
            END IF;
            IF ROW(NEW.id,NEW.workspace_id,NEW.project_id,NEW.owner_person_id)
                IS DISTINCT FROM ROW(OLD.id,OLD.workspace_id,OLD.project_id,OLD.owner_person_id) THEN
                RAISE EXCEPTION 'Family identity is immutable' USING ERRCODE='23514';
            END IF;
            IF NEW.pending_supplement_id IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM field_advance_requests WHERE id=NEW.pending_supplement_id
                AND family_id=NEW.id AND relation_kind='Supplement') THEN
                RAISE EXCEPTION 'Pending slot must reference a supplement in this family' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_family BEFORE UPDATE OR DELETE ON field_advance_families
            FOR EACH ROW EXECUTE FUNCTION advance_payment_guard_family();

        CREATE FUNCTION advance_payment_guard_installment_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='DELETE' THEN
                RAISE EXCEPTION 'Installment revision must be retained' USING ERRCODE='23514';
            END IF;
            IF (to_jsonb(NEW)-ARRAY['is_current','reviewed_by','reviewed_by_person_id','reviewed_at_utc'])
                IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['is_current','reviewed_by','reviewed_by_person_id','reviewed_at_utc'])
                OR (OLD.reviewed_at_utc IS NOT NULL AND (to_jsonb(NEW)-'is_current') IS DISTINCT FROM (to_jsonb(OLD)-'is_current')) THEN
                RAISE EXCEPTION 'Installment content requires a new revision' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER advance_payment_installment_revision BEFORE UPDATE OR DELETE ON field_advance_installment_revisions
            FOR EACH ROW EXECUTE FUNCTION advance_payment_guard_installment_revision();

        DO $$ DECLARE t text; BEGIN
            FOREACH t IN ARRAY ARRAY['reservation_entries','payment_results','payment_participations','payment_events'] LOOP
                EXECUTE format('CREATE TRIGGER payment_immutable BEFORE UPDATE OR DELETE ON field_advance_%I FOR EACH ROW EXECUTE FUNCTION advance_request_reject_history_mutation()',t);
            END LOOP;
            FOREACH t IN ARRAY ARRAY['families','payment_accounts','installments','installment_revisions','reservation_entries',
                'payment_attempts','payment_results','payment_proposals','payment_participations','payment_holds','payment_events','evidence','evidence_locations'] LOOP
                EXECUTE format('CREATE TRIGGER payment_no_truncate BEFORE TRUNCATE ON field_advance_%I FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation()',t);
            END LOOP;
        END $$;
        """;

    private const string RemovePaymentGuards = """
        LOCK TABLE field_advance_families,field_advance_requests,field_advance_payment_accounts,field_advance_installments,
            field_advance_installment_revisions,field_advance_reservation_entries,field_advance_payment_attempts,
            field_advance_payment_results,field_advance_payment_proposals,field_advance_payment_participations,
            field_advance_payment_holds,field_advance_payment_events,field_advance_evidence,field_advance_evidence_locations IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM field_advance_requests) OR EXISTS(SELECT 1 FROM field_advance_families) THEN
                RAISE EXCEPTION 'Cannot discard payment foundation with recorded requests' USING ERRCODE='23514';
            END IF;
        END $$;
        DROP FUNCTION advance_payment_initialize_request() CASCADE;
        DROP FUNCTION advance_payment_initialize_account() CASCADE;
        DROP FUNCTION advance_payment_normal_basis() CASCADE;
        DROP FUNCTION advance_payment_guard_request_family() CASCADE;
        DROP FUNCTION advance_payment_guard_account() CASCADE;
        DROP FUNCTION advance_payment_guard_family() CASCADE;
        DROP FUNCTION advance_payment_guard_installment_revision() CASCADE;
        """;
}
