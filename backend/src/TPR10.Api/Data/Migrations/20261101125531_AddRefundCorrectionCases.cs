using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundCorrectionCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "baseline_amount",
                table: "field_advance_settlement_adjustment_cases",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "baseline_version",
                table: "field_advance_settlement_adjustment_cases",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "root_refund_id",
                table: "field_advance_settlement_adjustment_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "supersedes_id",
                table: "field_advance_refund_corrections",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_case_closures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outstanding = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_case_closures", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_case_closures_workspace_id_project~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementCaseClosure_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_outstanding", "outstanding>=0 AND outstanding<=9999999999999999.99 AND outstanding=trunc(outstanding,2)");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_settled", "outstanding=0 AND accepted_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementCaseClosure_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCaseClosure_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.case_id },
                        principalTable: "field_advance_settlement_adjustment_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_case_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_case_payments", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_case_payments_workspace_id_project~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementCasePayment_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_amount_positive", "amount>0");
                    table.CheckConstraint("ck_st_SettlementCasePayment_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_paid_on", "isfinite(paid_on)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementCasePayment_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementCasePayment_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.case_id },
                        principalTable: "field_advance_settlement_adjustment_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementCasePayment_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.supersedes_id },
                        principalTable: "field_advance_settlement_case_payments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_refund_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases",
                sql: "(root_refund_id IS NULL AND baseline_version IS NULL AND baseline_amount IS NULL) OR (root_refund_id IS NOT NULL AND baseline_version IS NOT NULL AND baseline_version>0 AND baseline_amount IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline_amount",
                table: "field_advance_settlement_adjustment_cases",
                sql: "baseline_amount>=0 AND baseline_amount<=9999999999999999.99 AND baseline_amount=trunc(baseline_amount,2)");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_supersedes_id",
                table: "field_advance_refund_corrections",
                column: "supersedes_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_project_id_f~3",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_case_id",
                table: "field_advance_settlement_case_closures",
                column: "case_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_proposed_by",
                table: "field_advance_settlement_case_closures",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_reviewed_by",
                table: "field_advance_settlement_case_closures",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_workspace_id_command~",
                table: "field_advance_settlement_case_closures",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_workspace_id_project~",
                table: "field_advance_settlement_case_closures",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_workspace_id_propose~",
                table: "field_advance_settlement_case_closures",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_closures_workspace_id_reviewe~",
                table: "field_advance_settlement_case_closures",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_proposed_by",
                table: "field_advance_settlement_case_payments",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_reviewed_by",
                table: "field_advance_settlement_case_payments",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_supersedes_id",
                table: "field_advance_settlement_case_payments",
                column: "supersedes_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_command~",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_projec~1",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_projec~2",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_project~",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_propose~",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_case_payments_workspace_id_reviewe~",
                table: "field_advance_settlement_case_payments",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_st_RefundCorrection_9",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" },
                principalTable: "field_advance_refund_corrections",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_refund_id" },
                principalTable: "field_advance_refunds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                DO $$ DECLARE t text; BEGIN
                    FOREACH t IN ARRAY ARRAY['field_advance_settlement_case_payments','field_advance_settlement_case_closures'] LOOP
                        EXECUTE format('CREATE TRIGGER settlement_accepted_history BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION advance_settlement_guard_accepted()', t);
                        EXECUTE format('CREATE TRIGGER settlement_no_truncate BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation()', t);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_settlement_adjustment_cases, field_advance_refund_corrections,
                    field_advance_settlement_case_payments, field_advance_settlement_case_closures IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_settlement_case_payments)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_closures)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_adjustment_cases WHERE root_refund_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_refund_corrections WHERE supersedes_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Case history prevents destructive downgrade' USING ERRCODE='23514';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_st_RefundCorrection_9",
                table: "field_advance_refund_corrections");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_case_closures");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_case_payments");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline_amount",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_refund_corrections_supersedes_id",
                table: "field_advance_refund_corrections");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_project_id_f~3",
                table: "field_advance_refund_corrections");

            migrationBuilder.DropColumn(
                name: "baseline_amount",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropColumn(
                name: "baseline_version",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropColumn(
                name: "root_refund_id",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropColumn(
                name: "supersedes_id",
                table: "field_advance_refund_corrections");
        }
    }
}
