using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinEvidenceInputDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "input_sha256",
                table: "evidence_objects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_evidence_input",
                table: "evidence_objects",
                sql: "input_sha256 IS NULL OR input_sha256 ~ '^[a-f0-9]{64}$'");
            migrationBuilder.Sql("""
                CREATE FUNCTION evidence_preserve_input() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.input_sha256 IS NOT NULL AND NEW.input_sha256 IS DISTINCT FROM OLD.input_sha256 THEN
                        RAISE EXCEPTION 'Evidence input digest is immutable' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER evidence_input_guard BEFORE UPDATE ON evidence_objects FOR EACH ROW EXECUTE FUNCTION evidence_preserve_input();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE evidence_objects IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM evidence_objects WHERE input_sha256 IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot discard retained evidence input digest' USING ERRCODE='23514';
                    END IF;
                END $$;
                DROP TRIGGER evidence_input_guard ON evidence_objects;
                DROP FUNCTION evidence_preserve_input();
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_evidence_input",
                table: "evidence_objects");

            migrationBuilder.DropColumn(
                name: "input_sha256",
                table: "evidence_objects");
        }
    }
}
