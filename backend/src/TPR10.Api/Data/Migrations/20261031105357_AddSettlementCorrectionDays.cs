using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementCorrectionDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "correction_days",
                table: "field_advance_policy_revisions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_correction_days",
                table: "field_advance_policy_revisions",
                sql: "correction_days IS NULL OR correction_days>0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep values and reviewed evidence readable by the matching application model.
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_policy_revisions, field_advance_policy_reviews IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_policy_revisions WHERE correction_days IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_policy_reviews
                            WHERE reviewed_content_json->'Content'->>'CorrectionDays' IS NOT NULL) THEN
                        RAISE EXCEPTION 'ไม่สามารถย้อน migration ที่มีจำนวนวันแก้ไขหรือหลักฐานการตรวจแล้ว' USING ERRCODE='23514';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_correction_days",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropColumn(
                name: "correction_days",
                table: "field_advance_policy_revisions");
        }
    }
}
