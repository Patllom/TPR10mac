namespace TPR10.Api.Data.Migrations;

// Frozen migration SQL: do not call a mutable runtime service from migrations.
public partial class AddAttendanceEvidenceStorage
{
    private const string DowngradePreflight = """
        LOCK TABLE evidence_objects, evidence_locations, evidence_bindings, evidence_migration_jobs,
            evidence_migration_items, attendance_storage_locations, attendance_storage_write_target IN ACCESS EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM evidence_objects) OR EXISTS(SELECT 1 FROM evidence_locations)
                OR EXISTS(SELECT 1 FROM evidence_bindings) OR EXISTS(SELECT 1 FROM evidence_migration_jobs)
                OR EXISTS(SELECT 1 FROM evidence_migration_items) OR EXISTS(SELECT 1 FROM attendance_storage_locations)
                OR EXISTS(SELECT 1 FROM attendance_storage_write_target) THEN
                RAISE EXCEPTION 'Cannot downgrade attendance evidence/storage while retained records exist';
            END IF;
        END $$;
        """;

    private const string CreateGuards = """
        CREATE FUNCTION evidence_serialize_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF current_setting('transaction_isolation') <> 'read committed' THEN
                RAISE EXCEPTION 'Evidence writes require read committed isolation' USING ERRCODE='25001';
            END IF;
            PERFORM pg_advisory_xact_lock(7241002);
            RETURN NULL;
        END $$;
        CREATE FUNCTION evidence_no_remove() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Evidence/storage history cannot be removed' USING ERRCODE='23514';
        END $$;
        DO $$ DECLARE tab text;
        BEGIN
            FOREACH tab IN ARRAY ARRAY['evidence_objects','evidence_locations','evidence_bindings',
                'attendance_storage_locations','attendance_storage_write_target','evidence_migration_jobs','evidence_migration_items'] LOOP
                EXECUTE format('CREATE TRIGGER evidence_write_lock BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH STATEMENT EXECUTE FUNCTION evidence_serialize_write()',tab);
                EXECUTE format('CREATE TRIGGER evidence_no_delete BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION evidence_no_remove()',tab);
                EXECUTE format('CREATE TRIGGER evidence_no_truncate BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION evidence_no_remove()',tab);
            END LOOP;
        END $$;

        CREATE FUNCTION evidence_preserve_object() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF ROW(NEW.id,NEW.operation_id,NEW.owner_id,NEW.membership_id,NEW.workspace_id,NEW.department_id,
                    NEW.snapshot_at_utc,NEW.occurred_at_utc,NEW.action,NEW.storage_id,NEW.storage_version,NEW.object_key,NEW.created_at_utc)
                    IS DISTINCT FROM ROW(OLD.id,OLD.operation_id,OLD.owner_id,OLD.membership_id,OLD.workspace_id,OLD.department_id,
                    OLD.snapshot_at_utc,OLD.occurred_at_utc,OLD.action,OLD.storage_id,OLD.storage_version,OLD.object_key,OLD.created_at_utc) THEN
                    RAISE EXCEPTION 'Evidence identity and stamp are immutable' USING ERRCODE='23514';
                END IF;
                IF OLD.state<>'Reserved' AND
                    ROW(NEW.sha256,NEW.length,NEW.width,NEW.height,NEW.thumbnail_sha256,NEW.thumbnail_length,NEW.thumbnail_width,NEW.thumbnail_height)
                    IS DISTINCT FROM ROW(OLD.sha256,OLD.length,OLD.width,OLD.height,OLD.thumbnail_sha256,OLD.thumbnail_length,OLD.thumbnail_width,OLD.thumbnail_height) THEN
                    RAISE EXCEPTION 'Prepared evidence bytes are immutable' USING ERRCODE='23514';
                END IF;
                IF NEW.state<>OLD.state AND NOT ((OLD.state='Reserved' AND NEW.state IN ('Prepared','Orphan'))
                    OR (OLD.state='Prepared' AND NEW.state IN ('Published','Orphan'))) THEN
                    RAISE EXCEPTION 'Invalid evidence transition' USING ERRCODE='23514';
                END IF;
                IF NEW IS DISTINCT FROM OLD AND (NEW.version<=OLD.version OR NEW.fencing_version<OLD.fencing_version) THEN
                    RAISE EXCEPTION 'Evidence version must advance' USING ERRCODE='23514';
                END IF;
            ELSIF NEW.state<>'Reserved' THEN
                RAISE EXCEPTION 'Evidence must start reserved' USING ERRCODE='23514';
            END IF;
            -- Invalid owner/unit tuples are left to the composite FK (23503).
            IF EXISTS(SELECT 1 FROM employee_memberships m WHERE m.id=NEW.membership_id AND m.user_id=NEW.owner_id
                AND m.workspace_id=NEW.workspace_id AND m.department_id=NEW.department_id
                AND NOT (m.valid_from_utc<=NEW.snapshot_at_utc AND (m.valid_to_utc IS NULL OR NEW.snapshot_at_utc<m.valid_to_utc))) THEN
                RAISE EXCEPTION 'Evidence snapshot outside membership period' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER evidence_object_guard BEFORE INSERT OR UPDATE ON evidence_objects FOR EACH ROW EXECUTE FUNCTION evidence_preserve_object();

        CREATE FUNCTION evidence_preserve_copy() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW.id,NEW.evidence_id,NEW.storage_id,NEW.object_key,NEW.variant,NEW.sha256,NEW.length,NEW.created_at_utc)
                IS DISTINCT FROM ROW(OLD.id,OLD.evidence_id,OLD.storage_id,OLD.object_key,OLD.variant,OLD.sha256,OLD.length,OLD.created_at_utc)
                OR (OLD.verified_at_utc IS NOT NULL AND NEW.verified_at_utc IS DISTINCT FROM OLD.verified_at_utc) THEN
                RAISE EXCEPTION 'Copy identity and verified bytes are immutable' USING ERRCODE='23514';
            END IF;
            IF NEW IS DISTINCT FROM OLD AND NEW.version<=OLD.version THEN
                RAISE EXCEPTION 'Copy version must advance' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER evidence_copy_guard BEFORE UPDATE ON evidence_locations FOR EACH ROW EXECUTE FUNCTION evidence_preserve_copy();

        CREATE FUNCTION evidence_preserve_binding() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Published event binding is immutable' USING ERRCODE='23514';
        END $$;
        CREATE TRIGGER evidence_binding_guard BEFORE UPDATE ON evidence_bindings FOR EACH ROW EXECUTE FUNCTION evidence_preserve_binding();

        CREATE FUNCTION evidence_preserve_storage() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF ROW(NEW.id,NEW.alias,NEW.kind,NEW.config_fingerprint,NEW.created_at_utc)
                IS DISTINCT FROM ROW(OLD.id,OLD.alias,OLD.kind,OLD.config_fingerprint,OLD.created_at_utc) THEN
                RAISE EXCEPTION 'Registered storage identity cannot be repointed' USING ERRCODE='23514';
            END IF;
            IF NEW IS DISTINCT FROM OLD AND NEW.version<=OLD.version THEN
                RAISE EXCEPTION 'Storage version must advance' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER evidence_storage_guard BEFORE UPDATE ON attendance_storage_locations FOR EACH ROW EXECUTE FUNCTION evidence_preserve_storage();
        CREATE FUNCTION evidence_monotonic_target() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW IS DISTINCT FROM OLD AND NEW.version<=OLD.version THEN
                RAISE EXCEPTION 'Write target version must advance' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER evidence_target_guard BEFORE UPDATE ON attendance_storage_write_target FOR EACH ROW EXECUTE FUNCTION evidence_monotonic_target();

        CREATE FUNCTION evidence_validate_publication() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE eid uuid; obj evidence_objects%ROWTYPE;
        BEGIN
            IF TG_TABLE_NAME='evidence_objects' THEN eid:=NEW.id; ELSE eid:=NEW.evidence_id; END IF;
            SELECT * INTO obj FROM evidence_objects WHERE id=eid;
            IF NOT FOUND THEN RETURN NULL; END IF;
            IF EXISTS(SELECT 1 FROM evidence_locations l WHERE l.evidence_id=eid AND l.state IN ('Verified','Active','Fallback')
                AND (l.sha256 IS DISTINCT FROM CASE WHEN l.variant='full' THEN obj.sha256 ELSE obj.thumbnail_sha256 END
                    OR l.length IS DISTINCT FROM CASE WHEN l.variant='full' THEN obj.length ELSE obj.thumbnail_length END)) THEN
                RAISE EXCEPTION 'Verified copy must match original evidence bytes' USING ERRCODE='23514';
            END IF;
            IF obj.state='Published' THEN
                IF NOT EXISTS(SELECT 1 FROM evidence_bindings b WHERE b.evidence_id=eid)
                    OR (SELECT count(*) FROM evidence_locations l WHERE l.evidence_id=eid AND l.state='Active')<>2 THEN
                    RAISE EXCEPTION 'Publication requires binding and both verified active variants' USING ERRCODE='23514';
                END IF;
            ELSIF EXISTS(SELECT 1 FROM evidence_bindings b WHERE b.evidence_id=eid) THEN
                RAISE EXCEPTION 'Binding requires published evidence in the same transaction' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER evidence_publication_check AFTER INSERT OR UPDATE ON evidence_objects
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_validate_publication();
        CREATE CONSTRAINT TRIGGER evidence_publication_check AFTER INSERT OR UPDATE ON evidence_locations
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_validate_publication();
        CREATE CONSTRAINT TRIGGER evidence_publication_check AFTER INSERT OR UPDATE ON evidence_bindings
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION evidence_validate_publication();
        """;
}
