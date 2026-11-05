using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseExcess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_advance_settlement_excess_cases",
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
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    claimed_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    cap_amount = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_excess_cases", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_excess_cases_workspace_id_project_~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementExcessCase_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_amount_positive", "amount>0 AND claimed_amount>cap_amount AND amount=claimed_amount-cap_amount");
                    table.CheckConstraint("ck_st_SettlementExcessCase_cap_amount", "cap_amount>=0 AND cap_amount<=9999999999999999.99 AND cap_amount=trunc(cap_amount,2)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_claimed_amount", "claimed_amount>=0 AND claimed_amount<=9999999999999999.99 AND claimed_amount=trunc(claimed_amount,2)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessCase_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementExcessCase_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.document_id },
                        principalTable: "field_advance_expense_documents",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessCase_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_excess_dispositions",
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
                    excess_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_excess_dispositions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_excess_dispositions_workspace_id_p~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_code", "code IN ('AwaitingExternalReview','NoAdditionalPaymentRequired','FinancialSettlementRecorded')");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementExcessDisposition_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessDisposition_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.excess_id },
                        principalTable: "field_advance_settlement_excess_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_excess_payments",
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
                    excess_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_excess_payments", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_excess_payments_workspace_id_proje~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementExcessPayment_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_amount_positive", "amount>0");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_paid_on", "isfinite(paid_on)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementExcessPayment_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_4",
                        columns: x => new { x.workspace_id, x.recipient_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_5",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.excess_id },
                        principalTable: "field_advance_settlement_excess_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExcessPayment_9",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.supersedes_id },
                        principalTable: "field_advance_settlement_excess_payments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_document_id",
                table: "field_advance_settlement_excess_cases",
                column: "document_id",
                unique: true,
                filter: "state<>'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_proposed_by",
                table: "field_advance_settlement_excess_cases",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_reviewed_by",
                table: "field_advance_settlement_excess_cases",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_workspace_id_command_~",
                table: "field_advance_settlement_excess_cases",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_workspace_id_project_~",
                table: "field_advance_settlement_excess_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_workspace_id_project~1",
                table: "field_advance_settlement_excess_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_workspace_id_proposed~",
                table: "field_advance_settlement_excess_cases",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_cases_workspace_id_reviewed~",
                table: "field_advance_settlement_excess_cases",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_proposed_by",
                table: "field_advance_settlement_excess_dispositions",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_reviewed_by",
                table: "field_advance_settlement_excess_dispositions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_workspace_id_~1",
                table: "field_advance_settlement_excess_dispositions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_workspace_id_~2",
                table: "field_advance_settlement_excess_dispositions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "excess_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_workspace_id_c~",
                table: "field_advance_settlement_excess_dispositions",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_workspace_id_p~",
                table: "field_advance_settlement_excess_dispositions",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_dispositions_workspace_id_r~",
                table: "field_advance_settlement_excess_dispositions",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_proposed_by",
                table: "field_advance_settlement_excess_payments",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_reviewed_by",
                table: "field_advance_settlement_excess_payments",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_supersedes_id",
                table: "field_advance_settlement_excess_payments",
                column: "supersedes_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_comma~",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_proj~1",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "excess_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_proj~2",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_proje~",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_propo~",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_recip~",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "recipient_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_excess_payments_workspace_id_revie~",
                table: "field_advance_settlement_excess_payments",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });
            migrationBuilder.Sql("""
                DO $$ DECLARE t text; BEGIN
                    FOREACH t IN ARRAY ARRAY['settlement_excess_cases','settlement_excess_payments','settlement_excess_dispositions'] LOOP
                        EXECUTE format('CREATE TRIGGER settlement_accepted_history BEFORE UPDATE OR DELETE ON field_advance_%I FOR EACH ROW EXECUTE FUNCTION advance_settlement_guard_accepted()',t);
                        EXECUTE format('CREATE TRIGGER settlement_no_truncate BEFORE TRUNCATE ON field_advance_%I FOR EACH STATEMENT EXECUTE FUNCTION advance_request_reject_history_mutation()',t);
                    END LOOP;
                END $$;
                CREATE FUNCTION advance_excess_claim_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='DELETE' OR OLD.state='Resolved' OR OLD.command_id<>NEW.command_id OR OLD.created_at_utc<>NEW.created_at_utc
                        OR OLD.proposed_by<>NEW.proposed_by OR OLD.proposed_by_person_id<>NEW.proposed_by_person_id OR OLD.snapshot_json<>NEW.snapshot_json
                        OR OLD.amount<>NEW.amount OR OLD.claimed_amount<>NEW.claimed_amount OR OLD.cap_amount<>NEW.cap_amount
                        OR OLD.document_id<>NEW.document_id OR OLD.source_revision_id<>NEW.source_revision_id OR OLD.request_id<>NEW.request_id THEN
                        RAISE EXCEPTION 'Excess claim history is immutable' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER excess_claim_history BEFORE UPDATE OR DELETE ON field_advance_settlement_excess_cases
                    FOR EACH ROW EXECUTE FUNCTION advance_excess_claim_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_settlement_rounds, field_advance_settlement_adjustment_cases,
                    field_advance_refund_corrections, field_advance_settlement_case_payments,
                    field_advance_settlement_case_closures, field_advance_settlement_excess_cases IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_settlement_excess_cases)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_rounds WHERE round_kind='Adjustment')
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_payments)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_closures)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_adjustment_cases WHERE root_refund_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_refund_corrections WHERE supersedes_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Excess history prevents downgrade' USING ERRCODE='23514';
                    END IF;
                END $$;
                DROP TRIGGER excess_claim_history ON field_advance_settlement_excess_cases;
                DROP FUNCTION advance_excess_claim_guard();
                """);
            migrationBuilder.DropTable(
                name: "field_advance_settlement_excess_dispositions");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_excess_payments");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_excess_cases");
        }
    }
}
