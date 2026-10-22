using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvanceApprovalLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "approval_levels",
                table: "field_advance_grant_proposals",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "delegable_approval_levels",
                table: "field_advance_grant_proposals",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "delegable_primary_approval_levels",
                table: "field_advance_grant_proposals",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string[]>(
                name: "primary_approval_levels",
                table: "field_advance_grant_proposals",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_grant_level_authority",
                table: "field_advance_grant_proposals",
                sql: "(cardinality(approval_levels)=0 OR (project_id IS NOT NULL AND approval_limit IS NOT NULL AND 'request.approve'=ANY(capabilities)))\nAND (cardinality(delegable_approval_levels)=0 OR (project_id IS NOT NULL AND approval_limit IS NOT NULL AND 'request.approve'=ANY(delegable_capabilities)))\nAND array_position(approval_levels,NULL) IS NULL AND array_position(delegable_approval_levels,NULL) IS NULL\nAND array_position(primary_approval_levels,NULL) IS NULL AND array_position(delegable_primary_approval_levels,NULL) IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_grant_primary_levels",
                table: "field_advance_grant_proposals",
                sql: "primary_approval_levels <@ approval_levels AND delegable_primary_approval_levels <@ delegable_approval_levels");
            migrationBuilder.Sql("""
                CREATE FUNCTION field_advance_validate_approval_levels() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE levels text[];
                BEGIN
                    FOR levels IN SELECT value FROM (VALUES (NEW.approval_levels), (NEW.delegable_approval_levels),
                        (NEW.primary_approval_levels), (NEW.delegable_primary_approval_levels)) AS sets(value)
                    LOOP
                        IF cardinality(levels)>100 OR coalesce(array_ndims(levels),1)<>1
                            OR EXISTS (SELECT 1 FROM unnest(levels) AS code WHERE code IS NULL OR length(code) NOT BETWEEN 1 AND 64
                                OR code<>btrim(code) OR code ~ '[[:cntrl:]]' OR code ~ '^[[:space:]]*$')
                            OR cardinality(levels)<>(SELECT count(DISTINCT lower(code)) FROM unnest(levels) AS code) THEN
                            RAISE EXCEPTION 'Invalid approval level set' USING ERRCODE='23514';
                        END IF;
                    END LOOP;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER field_advance_approval_levels BEFORE INSERT OR UPDATE ON field_advance_grant_proposals
                    FOR EACH ROW EXECUTE FUNCTION field_advance_validate_approval_levels();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_grant_proposals IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_grant_proposals WHERE
                        cardinality(approval_levels)>0 OR cardinality(delegable_approval_levels)>0
                        OR cardinality(primary_approval_levels)>0 OR cardinality(delegable_primary_approval_levels)>0) THEN
                        RAISE EXCEPTION 'Cannot discard recorded approval authority' USING ERRCODE='23514';
                    END IF;
                END $$;
                DROP TRIGGER field_advance_approval_levels ON field_advance_grant_proposals;
                DROP FUNCTION field_advance_validate_approval_levels();
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_grant_level_authority",
                table: "field_advance_grant_proposals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_grant_primary_levels",
                table: "field_advance_grant_proposals");

            migrationBuilder.DropColumn(
                name: "approval_levels",
                table: "field_advance_grant_proposals");

            migrationBuilder.DropColumn(
                name: "delegable_approval_levels",
                table: "field_advance_grant_proposals");

            migrationBuilder.DropColumn(
                name: "delegable_primary_approval_levels",
                table: "field_advance_grant_proposals");

            migrationBuilder.DropColumn(
                name: "primary_approval_levels",
                table: "field_advance_grant_proposals");
        }
    }
}
