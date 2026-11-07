using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvanceSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "field_advance_expense_documents",
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
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    document_number = table.Column<string>(type: "text", nullable: true),
                    has_no_number = table.Column<bool>(type: "boolean", nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_expense_documents", x => x.id);
                    table.UniqueConstraint("AK_field_advance_expense_documents_workspace_id_project_id_fam~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_ExpenseDocument_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDocument_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_ExpenseDocument_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDocument_document", "length(btrim(issuer))>0 AND ((has_no_number AND document_number IS NULL) OR (NOT has_no_number AND document_number IS NOT NULL AND length(btrim(document_number))>0))");
                    table.CheckConstraint("ck_st_ExpenseDocument_issued_on", "isfinite(issued_on)");
                    table.CheckConstraint("ck_st_ExpenseDocument_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_ExpenseDocument_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDocument_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_ExpenseDocument_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDocument_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_incident_resolutions",
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
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_received = table.Column<decimal>(type: "numeric", nullable: false),
                    wrong_recipient_received = table.Column<decimal>(type: "numeric", nullable: false),
                    wrong_recipient_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_incident_resolutions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_payment_incident_resolutions_workspace_id_pro~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_owner_received", "owner_received>=0 AND owner_received<=9999999999999999.99 AND owner_received=trunc(owner_received,2)");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_recipient", "wrong_recipient_received=0 OR wrong_recipient_person_id IS NOT NULL");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_version", "version>0");
                    table.CheckConstraint("ck_st_PaymentIncidentResolution_wrong_recipient_received", "wrong_recipient_received>=0 AND wrong_recipient_received<=9999999999999999.99 AND wrong_recipient_received=trunc(wrong_recipient_received,2)");
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_5",
                        columns: x => new { x.workspace_id, x.wrong_recipient_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.hold_id },
                        principalTable: "field_advance_payment_holds",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentResolution_9",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.attempt_id },
                        principalTable: "field_advance_payment_attempts",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_rounds",
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
                    source_round_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_rounds", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_rounds_workspace_id_project_id_fam~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementRound_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRound_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRound_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementRound_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRound_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementRound_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRound_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_round_id },
                        principalTable: "field_advance_settlement_rounds",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_work_completions",
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
                    completed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    clearing_due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    refund_due_on = table.Column<DateOnly>(type: "date", nullable: false),
                    clearing_days = table.Column<int>(type: "integer", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_work_completions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_work_completions_workspace_id_project_id_fami~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_WorkCompletion_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_WorkCompletion_clearing_due_on", "isfinite(clearing_due_on)");
                    table.CheckConstraint("ck_st_WorkCompletion_completed_on", "isfinite(completed_on)");
                    table.CheckConstraint("ck_st_WorkCompletion_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_WorkCompletion_due", "clearing_days>=0 AND clearing_due_on=completed_on+clearing_days AND refund_due_on=clearing_due_on");
                    table.CheckConstraint("ck_st_WorkCompletion_refund_due_on", "isfinite(refund_due_on)");
                    table.CheckConstraint("ck_st_WorkCompletion_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_WorkCompletion_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_WorkCompletion_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_WorkCompletion_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_WorkCompletion_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.supersedes_id },
                        principalTable: "field_advance_work_completions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_expense_duplicate_cases",
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
                    other_workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    other_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    other_family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    other_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    other_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version = table.Column<long>(type: "bigint", nullable: false),
                    other_document_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_expense_duplicate_cases", x => x.id);
                    table.UniqueConstraint("AK_field_advance_expense_duplicate_cases_workspace_id_project_~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_duplicate", "document_id<>other_document_id AND document_version>0 AND other_document_version>0");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_ExpenseDuplicateCase_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_6",
                        columns: x => new { x.other_workspace_id, x.other_project_id, x.other_family_id, x.other_request_id, x.other_document_id },
                        principalTable: "field_advance_expense_documents",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseDuplicateCase_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.document_id },
                        principalTable: "field_advance_expense_documents",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_incident_recoveries",
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
                    resolution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    returned_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    returned_on = table.Column<DateOnly>(type: "date", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_incident_recoveries", x => x.id);
                    table.UniqueConstraint("AK_field_advance_payment_incident_recoveries_workspace_id_proj~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_recovery", "amount>0 AND return_kind IN ('Owner','WrongRecipient')");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_returned_on", "isfinite(returned_on)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_PaymentIncidentRecovery_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_4",
                        columns: x => new { x.workspace_id, x.returned_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_5",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_PaymentIncidentRecovery_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.resolution_id },
                        principalTable: "field_advance_payment_incident_resolutions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_revisions",
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
                    round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    source_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    correction_due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    total = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_revisions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_revisions_workspace_id_project_id_~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementRevision_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRevision_confirmed_at_utc", "isfinite(confirmed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRevision_correction_due_on", "isfinite(correction_due_on)");
                    table.CheckConstraint("ck_st_SettlementRevision_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRevision_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementRevision_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRevision_revision", "revision_number>0 AND ((confirmed_by IS NULL AND confirmed_by_person_id IS NULL AND confirmed_at_utc IS NULL) OR (confirmed_by IS NOT NULL AND confirmed_by_person_id IS NOT NULL AND confirmed_at_utc IS NOT NULL))");
                    table.CheckConstraint("ck_st_SettlementRevision_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementRevision_submitted_at_utc", "isfinite(submitted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementRevision_total", "total>=0 AND total<=9999999999999999.99 AND total=trunc(total,2)");
                    table.CheckConstraint("ck_st_SettlementRevision_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_0",
                        column: x => x.confirmed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_1",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_2",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_3",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_4",
                        columns: x => new { x.workspace_id, x.confirmed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_5",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_6",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.round_id },
                        principalTable: "field_advance_settlement_rounds",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementRevision_9",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_submission_declarations",
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
                    work_completion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supersedes_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_withdrawal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_submission_declarations", x => x.id);
                    table.UniqueConstraint("AK_field_advance_submission_declarations_workspace_id_project_~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SubmissionDeclaration_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SubmissionDeclaration_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SubmissionDeclaration_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SubmissionDeclaration_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SubmissionDeclaration_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SubmissionDeclaration_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.supersedes_id },
                        principalTable: "field_advance_submission_declarations",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SubmissionDeclaration_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.work_completion_id },
                        principalTable: "field_advance_work_completions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_approval_steps",
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
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    level_code = table.Column<string>(type: "text", nullable: false),
                    assignee_person_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_approval_steps", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_approval_steps_workspace_id_projec~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementApprovalStep_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_step", "(step_order=1 AND level_code='Supervisor') OR (step_order=2 AND level_code='ProjectManager') OR (step_order=3 AND level_code='Finance')");
                    table.CheckConstraint("ck_st_SettlementApprovalStep_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_2",
                        columns: x => new { x.workspace_id, x.assignee_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_3",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_4",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_5",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementApprovalStep_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_expense_adjustments",
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
                    source_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_version = table.Column<long>(type: "bigint", nullable: false),
                    before_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    after_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    delta = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_expense_adjustments", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_expense_adjustments_workspace_id_p~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_adjustment", "base_version>0 AND after_amount=before_amount+delta");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_after_amount", "after_amount>=0 AND after_amount<=9999999999999999.99 AND after_amount=trunc(after_amount,2)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_before_amount", "before_amount>=0 AND before_amount<=9999999999999999.99 AND before_amount=trunc(before_amount,2)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_delta", "delta>=-9999999999999999.99 AND delta<=9999999999999999.99 AND delta=trunc(delta,2)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementExpenseAdjustment_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementExpenseAdjustment_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_lines",
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
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    incurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    rate = table.Column<decimal>(type: "numeric", nullable: true),
                    lump_sum = table.Column<decimal>(type: "numeric", nullable: true),
                    amount = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_lines", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_lines_workspace_id_project_id_fam~1", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id, x.id });
                    table.UniqueConstraint("AK_field_advance_settlement_lines_workspace_id_project_id_fami~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementLine_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementLine_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_SettlementLine_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementLine_incurred_on", "isfinite(incurred_on)");
                    table.CheckConstraint("ck_st_SettlementLine_line", "(quantity IS NOT NULL AND rate IS NOT NULL AND lump_sum IS NULL AND quantity>0 AND rate>0 AND amount=round(quantity*rate,2)) OR (quantity IS NULL AND rate IS NULL AND lump_sum IS NOT NULL AND lump_sum>0 AND amount=lump_sum)");
                    table.CheckConstraint("ck_st_SettlementLine_lump_sum", "lump_sum>=0 AND lump_sum<=9999999999999999.99 AND lump_sum=trunc(lump_sum,2)");
                    table.CheckConstraint("ck_st_SettlementLine_quantity", "quantity>=0 AND quantity<=9999999999999999.9999 AND quantity=trunc(quantity,4)");
                    table.CheckConstraint("ck_st_SettlementLine_rate", "rate>=0 AND rate<=9999999999999999.99 AND rate=trunc(rate,2)");
                    table.CheckConstraint("ck_st_SettlementLine_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementLine_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementLine_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementLine_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementLine_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_line_id },
                        principalTable: "field_advance_settlement_lines",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_closure_records",
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
                    request_version = table.Column<long>(type: "bigint", nullable: false),
                    family_version = table.Column<long>(type: "bigint", nullable: false),
                    work_completion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    declaration_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_closure_records", x => x.id);
                    table.UniqueConstraint("AK_field_advance_closure_records_workspace_id_project_id_famil~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_ClosureRecord_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_ClosureRecord_closure", "request_version>0 AND family_version>0 AND accepted_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_st_ClosureRecord_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_ClosureRecord_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_ClosureRecord_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_ClosureRecord_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_ClosureRecord_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.declaration_id },
                        principalTable: "field_advance_submission_declarations",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ClosureRecord_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.work_completion_id },
                        principalTable: "field_advance_work_completions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_expense_allocations",
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
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_expense_allocations", x => x.id);
                    table.UniqueConstraint("AK_field_advance_expense_allocations_workspace_id_project_id_f~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_ExpenseAllocation_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseAllocation_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_ExpenseAllocation_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseAllocation_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_ExpenseAllocation_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_ExpenseAllocation_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_ExpenseAllocation_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.document_id },
                        principalTable: "field_advance_expense_documents",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_settlement_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_ExpenseAllocation_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.revision_id, x.line_id },
                        principalTable: "field_advance_settlement_lines",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_settlement_adjustment_cases",
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
                    closure_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    external_paid_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    external_evidence_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_settlement_adjustment_cases", x => x.id);
                    table.UniqueConstraint("AK_field_advance_settlement_adjustment_cases_workspace_id_proj~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_external_paid_amount", "external_paid_amount>=0 AND external_paid_amount<=9999999999999999.99 AND external_paid_amount=trunc(external_paid_amount,2)");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_SettlementAdjustmentCase_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.closure_record_id },
                        principalTable: "field_advance_closure_records",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_SettlementAdjustmentCase_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.external_evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_refunds",
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
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    refunded_on = table.Column<DateOnly>(type: "date", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    adjustment_case_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_refunds", x => x.id);
                    table.UniqueConstraint("AK_field_advance_refunds_workspace_id_project_id_family_id_req~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_Refund_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_Refund_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_st_Refund_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_Refund_destination_snapshot_json", "jsonb_typeof(destination_snapshot_json)='object'");
                    table.CheckConstraint("ck_st_Refund_refund", "amount>0 AND channel IN ('Cash','Transfer')");
                    table.CheckConstraint("ck_st_Refund_refunded_on", "isfinite(refunded_on)");
                    table.CheckConstraint("ck_st_Refund_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_Refund_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_Refund_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_Refund_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_Refund_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.adjustment_case_id },
                        principalTable: "field_advance_settlement_adjustment_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_Refund_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_refund_corrections",
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
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_version = table.Column<long>(type: "bigint", nullable: false),
                    before_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    after_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    delta = table.Column<decimal>(type: "numeric", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    adjustment_case_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_refund_corrections", x => x.id);
                    table.UniqueConstraint("AK_field_advance_refund_corrections_workspace_id_project_id_fa~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_st_RefundCorrection_accepted_at_utc", "isfinite(accepted_at_utc)");
                    table.CheckConstraint("ck_st_RefundCorrection_adjustment", "base_version>0 AND after_amount=before_amount+delta");
                    table.CheckConstraint("ck_st_RefundCorrection_after_amount", "after_amount>=0 AND after_amount<=9999999999999999.99 AND after_amount=trunc(after_amount,2)");
                    table.CheckConstraint("ck_st_RefundCorrection_before_amount", "before_amount>=0 AND before_amount<=9999999999999999.99 AND before_amount=trunc(before_amount,2)");
                    table.CheckConstraint("ck_st_RefundCorrection_created_at_utc", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_st_RefundCorrection_delta", "delta>=-9999999999999999.99 AND delta<=9999999999999999.99 AND delta=trunc(delta,2)");
                    table.CheckConstraint("ck_st_RefundCorrection_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_st_RefundCorrection_reviewed_at_utc", "isfinite(reviewed_at_utc)");
                    table.CheckConstraint("ck_st_RefundCorrection_snapshot_json", "jsonb_typeof(snapshot_json)='object'");
                    table.CheckConstraint("ck_st_RefundCorrection_version", "version>0");
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_3",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_4",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.adjustment_case_id },
                        principalTable: "field_advance_settlement_adjustment_cases",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_st_RefundCorrection_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.refund_id },
                        principalTable: "field_advance_refunds",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_proposed_by",
                table: "field_advance_closure_records",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_request_id",
                table: "field_advance_closure_records",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_reviewed_by",
                table: "field_advance_closure_records",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_workspace_id_command_id",
                table: "field_advance_closure_records",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_workspace_id_project_id_fami~1",
                table: "field_advance_closure_records",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "work_completion_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_workspace_id_project_id_famil~",
                table: "field_advance_closure_records",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "declaration_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_workspace_id_proposed_by_pers~",
                table: "field_advance_closure_records",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_closure_records_workspace_id_reviewed_by_pers~",
                table: "field_advance_closure_records",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_document_id_line_id",
                table: "field_advance_expense_allocations",
                columns: new[] { "document_id", "line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_proposed_by",
                table: "field_advance_expense_allocations",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_reviewed_by",
                table: "field_advance_expense_allocations",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_workspace_id_command_id",
                table: "field_advance_expense_allocations",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_workspace_id_project_id_~1",
                table: "field_advance_expense_allocations",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id", "line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_workspace_id_project_id_f~",
                table: "field_advance_expense_allocations",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_workspace_id_proposed_by_~",
                table: "field_advance_expense_allocations",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_allocations_workspace_id_reviewed_by_~",
                table: "field_advance_expense_allocations",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_proposed_by",
                table: "field_advance_expense_documents",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_reviewed_by",
                table: "field_advance_expense_documents",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_workspace_id_command_id",
                table: "field_advance_expense_documents",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_workspace_id_fingerprint",
                table: "field_advance_expense_documents",
                columns: new[] { "workspace_id", "fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_workspace_id_project_id_fam~",
                table: "field_advance_expense_documents",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_workspace_id_proposed_by_pe~",
                table: "field_advance_expense_documents",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_documents_workspace_id_reviewed_by_pe~",
                table: "field_advance_expense_documents",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_other_workspace_id_ot~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "other_workspace_id", "other_project_id", "other_family_id", "other_request_id", "other_document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_proposed_by",
                table: "field_advance_expense_duplicate_cases",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_reviewed_by",
                table: "field_advance_expense_duplicate_cases",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_command_~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_project_~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_proposed~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_reviewed~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_proposed_by",
                table: "field_advance_payment_incident_recoveries",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_reviewed_by",
                table: "field_advance_payment_incident_recoveries",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_comm~",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_pro~1",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "resolution_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_proj~",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_prop~",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_retu~",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "returned_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_recoveries_workspace_id_revi~",
                table: "field_advance_payment_incident_recoveries",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_attempt_id",
                table: "field_advance_payment_incident_resolutions",
                column: "attempt_id",
                unique: true,
                filter: "accepted_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_proposed_by",
                table: "field_advance_payment_incident_resolutions",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_reviewed_by",
                table: "field_advance_payment_incident_resolutions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_com~",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pr~1",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "project_id", "family_id", "hold_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pr~2",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pr~3",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pro~",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_rev~",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_wro~",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "wrong_recipient_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_proposed_by",
                table: "field_advance_refund_corrections",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_refund_id_base_version",
                table: "field_advance_refund_corrections",
                columns: new[] { "refund_id", "base_version" },
                unique: true,
                filter: "accepted_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_reviewed_by",
                table: "field_advance_refund_corrections",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_command_id",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_project_id_f~1",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_project_id_f~2",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "refund_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_project_id_fa~",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_proposed_by_p~",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refund_corrections_workspace_id_reviewed_by_p~",
                table: "field_advance_refund_corrections",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_proposed_by",
                table: "field_advance_refunds",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_reviewed_by",
                table: "field_advance_refunds",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_workspace_id_command_id",
                table: "field_advance_refunds",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_workspace_id_project_id_family_id_re~1",
                table: "field_advance_refunds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_workspace_id_project_id_family_id_req~",
                table: "field_advance_refunds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_workspace_id_proposed_by_person_id",
                table: "field_advance_refunds",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_refunds_workspace_id_reviewed_by_person_id",
                table: "field_advance_refunds",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_proposed_by",
                table: "field_advance_settlement_adjustment_cases",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_reviewed_by",
                table: "field_advance_settlement_adjustment_cases",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_comm~",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~1",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "external_evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_proj~",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "closure_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_prop~",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_revi~",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_proposed_by",
                table: "field_advance_settlement_approval_steps",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_reviewed_by",
                table: "field_advance_settlement_approval_steps",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_revision_id_reviewe~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "revision_id", "reviewed_by_person_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_revision_id_step_or~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "revision_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_workspace_id_assign~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "workspace_id", "assignee_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_workspace_id_comman~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_workspace_id_projec~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_workspace_id_propos~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_approval_steps_workspace_id_review~",
                table: "field_advance_settlement_approval_steps",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_proposed_by",
                table: "field_advance_settlement_expense_adjustments",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_reviewed_by",
                table: "field_advance_settlement_expense_adjustments",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_revision_id",
                table: "field_advance_settlement_expense_adjustments",
                column: "revision_id",
                unique: true,
                filter: "accepted_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_source_revisio~",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "source_revision_id", "base_version" },
                unique: true,
                filter: "accepted_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~1",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~2",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_c~",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_p~",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_r~",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_proposed_by",
                table: "field_advance_settlement_lines",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_reviewed_by",
                table: "field_advance_settlement_lines",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_workspace_id_command_id",
                table: "field_advance_settlement_lines",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_workspace_id_project_id_fami~",
                table: "field_advance_settlement_lines",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_line_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_workspace_id_proposed_by_per~",
                table: "field_advance_settlement_lines",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_lines_workspace_id_reviewed_by_per~",
                table: "field_advance_settlement_lines",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_confirmed_by",
                table: "field_advance_settlement_revisions",
                column: "confirmed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_proposed_by",
                table: "field_advance_settlement_revisions",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_reviewed_by",
                table: "field_advance_settlement_revisions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_round_id",
                table: "field_advance_settlement_revisions",
                column: "round_id",
                unique: true,
                filter: "accepted_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_round_id_revision_number",
                table: "field_advance_settlement_revisions",
                columns: new[] { "round_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_command_id",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_confirmed_b~",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "confirmed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_project_id_~",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "round_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_project_id~1",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_proposed_by~",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_revisions_workspace_id_reviewed_by~",
                table: "field_advance_settlement_revisions",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_proposed_by",
                table: "field_advance_settlement_rounds",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_reviewed_by",
                table: "field_advance_settlement_rounds",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_command_id",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_project_id_fam~",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_round_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_proposed_by_pe~",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_reviewed_by_pe~",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_proposed_by",
                table: "field_advance_submission_declarations",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_reviewed_by",
                table: "field_advance_submission_declarations",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_workspace_id_command_~",
                table: "field_advance_submission_declarations",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_workspace_id_project_~",
                table: "field_advance_submission_declarations",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_workspace_id_project~1",
                table: "field_advance_submission_declarations",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "work_completion_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_workspace_id_proposed~",
                table: "field_advance_submission_declarations",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_submission_declarations_workspace_id_reviewed~",
                table: "field_advance_submission_declarations",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_proposed_by",
                table: "field_advance_work_completions",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_reviewed_by",
                table: "field_advance_work_completions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_workspace_id_command_id",
                table: "field_advance_work_completions",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_workspace_id_project_id_fami~",
                table: "field_advance_work_completions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "supersedes_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_workspace_id_proposed_by_per~",
                table: "field_advance_work_completions",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_work_completions_workspace_id_reviewed_by_per~",
                table: "field_advance_work_completions",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });
            migrationBuilder.Sql(SettlementGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SettlementDowngradeGuard);
            migrationBuilder.DropTable(
                name: "field_advance_expense_allocations");

            migrationBuilder.DropTable(
                name: "field_advance_expense_duplicate_cases");

            migrationBuilder.DropTable(
                name: "field_advance_payment_incident_recoveries");

            migrationBuilder.DropTable(
                name: "field_advance_refund_corrections");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_approval_steps");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_lines");

            migrationBuilder.DropTable(
                name: "field_advance_expense_documents");

            migrationBuilder.DropTable(
                name: "field_advance_payment_incident_resolutions");

            migrationBuilder.DropTable(
                name: "field_advance_refunds");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_revisions");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropTable(
                name: "field_advance_settlement_rounds");

            migrationBuilder.DropTable(
                name: "field_advance_closure_records");

            migrationBuilder.DropTable(
                name: "field_advance_submission_declarations");

            migrationBuilder.DropTable(
                name: "field_advance_work_completions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds");
            migrationBuilder.Sql("DROP FUNCTION advance_settlement_guard_document(); DROP FUNCTION advance_settlement_guard_revision_child(); DROP FUNCTION advance_settlement_guard_accepted();");
        }
    }
}
