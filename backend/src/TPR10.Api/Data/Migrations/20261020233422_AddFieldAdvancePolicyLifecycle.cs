using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvancePolicyLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_state",
                table: "field_advance_policy_revisions");

            migrationBuilder.AddColumn<Guid>(
                name: "expected_active_revision_id",
                table: "field_advance_policy_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "expected_active_version",
                table: "field_advance_policy_revisions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "field_advance_policy_revisions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_policy_revision_id",
                table: "field_advance_policy_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewed_content_hash",
                table: "field_advance_policy_reviews",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewed_content_json",
                table: "field_advance_policy_reviews",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reviewed_version",
                table: "field_advance_policy_reviews",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_scope_project_~1",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "scope_project_id", "source_policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_scope_project_i~",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "scope_project_id", "expected_active_revision_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_expected_active",
                table: "field_advance_policy_revisions",
                sql: "(expected_active_revision_id IS NULL AND expected_active_version=0) OR (expected_active_revision_id IS NOT NULL AND expected_active_version>=1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_mode",
                table: "field_advance_policy_revisions",
                sql: "mode IS NULL OR (project_id IS NULL AND mode='Company') OR (project_id IS NOT NULL AND mode IN ('Override','Inherit'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_source",
                table: "field_advance_policy_revisions",
                sql: "source_policy_revision_id IS NULL OR source_policy_revision_id<>id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_state",
                table: "field_advance_policy_revisions",
                sql: "state IN ('Draft','Pending','Approved','Active','Rejected','Retired','Returned')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_review_evidence",
                table: "field_advance_policy_reviews",
                sql: "(reviewed_version IS NULL AND reviewed_content_hash IS NULL AND reviewed_content_json IS NULL) OR (reviewed_version IS NOT NULL AND reviewed_version>=1 AND reviewed_content_hash IS NOT NULL AND reviewed_content_hash ~ '^[a-f0-9]{64}$' AND reviewed_content_json IS NOT NULL AND jsonb_typeof(reviewed_content_json)='object')");

            migrationBuilder.AddForeignKey(
                name: "fk_policy_expectedactiverevisionid",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "scope_project_id", "expected_active_revision_id" },
                principalTable: "field_advance_policy_revisions",
                principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_policy_sourcepolicyrevisionid",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "scope_project_id", "source_policy_revision_id" },
                principalTable: "field_advance_policy_revisions",
                principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrading must not erase provenance that only this migration can represent.
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_policy_revisions, field_advance_policy_reviews IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_policy_revisions
                        WHERE mode IS NOT NULL OR source_policy_revision_id IS NOT NULL
                           OR expected_active_revision_id IS NOT NULL OR expected_active_version<>0 OR state='Returned')
                        OR EXISTS (SELECT 1 FROM field_advance_policy_reviews
                            WHERE reviewed_version IS NOT NULL OR reviewed_content_hash IS NOT NULL OR reviewed_content_json IS NOT NULL) THEN
                        RAISE EXCEPTION 'ไม่สามารถย้อน migration ที่มีหลักฐานวงจรกติกาแล้ว' USING ERRCODE='23514';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_policy_expectedactiverevisionid",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropForeignKey(
                name: "fk_policy_sourcepolicyrevisionid",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_scope_project_~1",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_scope_project_i~",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_expected_active",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_mode",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_source",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_policy_state",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_field_advance_review_evidence",
                table: "field_advance_policy_reviews");

            migrationBuilder.DropColumn(
                name: "expected_active_revision_id",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropColumn(
                name: "expected_active_version",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropColumn(
                name: "source_policy_revision_id",
                table: "field_advance_policy_revisions");

            migrationBuilder.DropColumn(
                name: "reviewed_content_hash",
                table: "field_advance_policy_reviews");

            migrationBuilder.DropColumn(
                name: "reviewed_content_json",
                table: "field_advance_policy_reviews");

            migrationBuilder.DropColumn(
                name: "reviewed_version",
                table: "field_advance_policy_reviews");

            migrationBuilder.AddCheckConstraint(
                name: "ck_field_advance_policy_state",
                table: "field_advance_policy_revisions",
                sql: "state IN ('Draft','Pending','Approved','Active','Rejected','Retired')");
        }
    }
}
