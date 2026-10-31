using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvancePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "family_id",
                table: "field_advance_requests",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<string>(
                name: "relation_kind",
                table: "field_advance_requests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<Guid>(
                name: "supplement_of_request_id",
                table: "field_advance_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "approval_basis_amount",
                table: "field_advance_request_revisions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "approval_basis_json",
                table: "field_advance_request_revisions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_field_advance_requests_workspace_id_project_id_family_id_id",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "field_advance_families",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    pending_supplement_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_families", x => x.id);
                    table.UniqueConstraint("AK_field_advance_families_workspace_id_project_id_id", x => new { x.workspace_id, x.project_id, x.id });
                    table.UniqueConstraint("AK_field_advance_families_workspace_id_project_id_id_owner_per~", x => new { x.workspace_id, x.project_id, x.id, x.owner_person_id });
                    table.CheckConstraint("ck_families_version", "version>0");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceFamily_0",
                        columns: x => new { x.workspace_id, x.owner_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceFamily_1",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceFamily_2",
                        columns: x => new { x.workspace_id, x.project_id, x.id, x.pending_supplement_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_installments", x => x.id);
                    table.UniqueConstraint("AK_field_advance_installments_workspace_id_project_id_family_i~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_installment_state", "state IN ('Draft','PendingReview','Returned','Reviewed','Paying','OutcomePending','CancelPending','Cancelled','Paid')");
                    table.CheckConstraint("ck_installments_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_installments_version", "version>0");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallment_0",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallment_1",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallment_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallment_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved = table.Column<decimal>(type: "numeric", nullable: false),
                    spendable = table.Column<decimal>(type: "numeric", nullable: false),
                    reserved = table.Column<decimal>(type: "numeric", nullable: false),
                    paid = table.Column<decimal>(type: "numeric", nullable: false),
                    ever_paid = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_accounts", x => x.id);
                    table.CheckConstraint("ck_payment_account_balance", "paid+reserved<=spendable AND spendable<=approved AND (paid=0 OR ever_paid) AND ((approved_revision_id IS NULL AND approved=0) OR (approved_revision_id IS NOT NULL AND approved>0))");
                    table.CheckConstraint("ck_payment_accounts_approved", "approved>=0 AND approved<=9999999999999999.99 AND approved=trunc(approved,2)");
                    table.CheckConstraint("ck_payment_accounts_paid", "paid>=0 AND paid<=9999999999999999.99 AND paid=trunc(paid,2)");
                    table.CheckConstraint("ck_payment_accounts_reserved", "reserved>=0 AND reserved<=9999999999999999.99 AND reserved=trunc(reserved,2)");
                    table.CheckConstraint("ck_payment_accounts_spendable", "spendable>=0 AND spendable<=9999999999999999.99 AND spendable=trunc(spendable,2)");
                    table.CheckConstraint("ck_payment_accounts_version", "version>0");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAccount_0",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAccount_1",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAccount_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAccount_3",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id, x.approved_revision_id },
                        principalTable: "field_advance_request_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_participations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_participations", x => x.id);
                    table.CheckConstraint("ck_payment_participation", "role IN ('Owner','Delegate','Approver','Checker','Payer')");
                    table.CheckConstraint("ck_payment_participations_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentParticipation_0",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentParticipation_1",
                        columns: x => new { x.workspace_id, x.person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentParticipation_2",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentParticipation_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_evidence", x => x.id);
                    table.UniqueConstraint("AK_field_advance_evidence_workspace_id_project_id_family_id_re~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.id });
                    table.CheckConstraint("ck_evidence_hash", "sha256 ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_evidence_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_payment_evidence", "length>0 AND length(btrim(content_type))>0 AND state IN ('Pending','Ready','Rejected','Missing')");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_0",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_1",
                        columns: x => new { x.workspace_id, x.created_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_2",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidence_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_installment_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    recipient_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_code = table.Column<string>(type: "text", nullable: true),
                    account_number = table.Column<string>(type: "text", nullable: true),
                    account_holder = table.Column<string>(type: "text", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    reviewed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_installment_revisions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_installment_revisions_workspace_id_project_id~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.id });
                    table.CheckConstraint("ck_installment_destination", "(channel='Cash' AND bank_code IS NULL AND account_number IS NULL AND account_holder IS NULL) OR (channel='Transfer' AND bank_code IS NOT NULL AND account_number IS NOT NULL AND account_holder IS NOT NULL AND length(btrim(bank_code))>0 AND length(btrim(account_number))>0 AND length(btrim(account_holder))>0)");
                    table.CheckConstraint("ck_installment_review", "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL AND isfinite(reviewed_at_utc)))");
                    table.CheckConstraint("ck_installment_revision", "revision_number>0 AND amount>0 AND length(btrim(reason))>0");
                    table.CheckConstraint("ck_installment_revisions_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_installment_revisions_hash", "content_hash ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_installment_revisions_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_0",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_2",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_3",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.recipient_person_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id", "owner_person_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceInstallmentRevision_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    family_version = table.Column<long>(type: "bigint", nullable: false),
                    request_version = table.Column<long>(type: "bigint", nullable: false),
                    installment_version = table.Column<long>(type: "bigint", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_events", x => x.id);
                    table.CheckConstraint("ck_payment_event", "family_version>0 AND request_version>0 AND (installment_version IS NULL OR installment_version>0) AND length(btrim(kind))>0");
                    table.CheckConstraint("ck_payment_events_data_json", "jsonb_typeof(data_json)='object'");
                    table.CheckConstraint("ck_payment_events_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_0",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_1",
                        columns: x => new { x.workspace_id, x.actor_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_3",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentEvent_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    released_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_holds", x => x.id);
                    table.CheckConstraint("ck_payment_hold", "length(btrim(code))>0 AND length(btrim(reason))>0 AND (installment_id IS NULL OR request_id IS NOT NULL) AND (code<>'PaymentMismatch' OR (request_id IS NULL AND installment_id IS NULL AND released_at_utc IS NULL)) AND ((released_at_utc IS NULL AND released_by_person_id IS NULL) OR (released_at_utc IS NOT NULL AND isfinite(released_at_utc) AND released_by_person_id IS NOT NULL))");
                    table.CheckConstraint("ck_payment_holds_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_0",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_1",
                        columns: x => new { x.workspace_id, x.created_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_2",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_3",
                        columns: x => new { x.workspace_id, x.released_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentHold_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    family_version = table.Column<long>(type: "bigint", nullable: false),
                    request_version = table.Column<long>(type: "bigint", nullable: false),
                    installment_version = table.Column<long>(type: "bigint", nullable: true),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_proposals", x => x.id);
                    table.CheckConstraint("ck_payment_proposal", "family_version>0 AND request_version>0 AND (installment_version IS NULL OR installment_version>0) AND state IN ('Pending','Approved','Returned','Rejected') AND length(btrim(kind))>0 AND length(btrim(reason))>0 AND ((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL AND isfinite(reviewed_at_utc)))");
                    table.CheckConstraint("ck_payment_proposals_after_json", "jsonb_typeof(after_json)='object'");
                    table.CheckConstraint("ck_payment_proposals_before_json", "jsonb_typeof(before_json)='object'");
                    table.CheckConstraint("ck_payment_proposals_evidence_ids_json", "jsonb_typeof(evidence_ids_json)='array'");
                    table.CheckConstraint("ck_payment_proposals_hash", "content_hash ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_payment_proposals_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_0",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_1",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_2",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_3",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_4",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_5",
                        columns: x => new { x.workspace_id, x.reviewed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentProposal_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_reservation_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_reservation_entries", x => x.id);
                    table.CheckConstraint("ck_reservation_entries_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_reservation_entries_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_reservation_entry", "action IN ('Reserve','Release','Consume') AND amount>0");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceReservationEntry_0",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceReservationEntry_1",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceReservationEntry_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceReservationEntry_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceReservationEntry_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_evidence_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_evidence_locations", x => x.id);
                    table.CheckConstraint("ck_evidence_location", "length(btrim(storage_key))>0 AND (NOT is_current OR verified_at_utc IS NOT NULL) AND (verified_at_utc IS NULL OR isfinite(verified_at_utc))");
                    table.CheckConstraint("ck_evidence_locations_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidenceLocation_0",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidenceLocation_1",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidenceLocation_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvanceEvidenceLocation_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.evidence_id },
                        principalTable: "field_advance_evidence",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_attempts", x => x.id);
                    table.UniqueConstraint("AK_field_advance_payment_attempts_workspace_id_project_id_fami~", x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.id });
                    table.CheckConstraint("ck_payment_attempt_state", "state IN ('Open','Unknown','Mismatch','Unpaid','Matched')");
                    table.CheckConstraint("ck_payment_attempts_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_0",
                        column: x => x.payer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_1",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_2",
                        columns: x => new { x.workspace_id, x.payer_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_3",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentAttempt_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.revision_id },
                        principalTable: "field_advance_installment_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    recipient_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: true),
                    evidence_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_results", x => x.id);
                    table.CheckConstraint("ck_payment_result", "amount>0 AND isfinite(paid_on)");
                    table.CheckConstraint("ck_payment_results_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_payment_results_evidence_ids_json", "jsonb_typeof(evidence_ids_json)='array'");
                    table.CheckConstraint("ck_payment_results_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_0",
                        column: x => x.recorded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_1",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_2",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_3",
                        columns: x => new { x.workspace_id, x.recipient_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_4",
                        columns: x => new { x.workspace_id, x.recorded_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_6",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_7",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_AdvancePaymentResult_8",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.attempt_id },
                        principalTable: "field_advance_payment_attempts",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_family_id",
                table: "field_advance_requests",
                column: "family_id",
                unique: true,
                filter: "relation_kind='Normal'");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_workspace_id_project_id_family_id_ow~",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "family_id", "owner_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_workspace_id_project_id_family_id_su~",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "family_id", "supplement_of_request_id" });

            migrationBuilder.Sql(BackfillPayments);

            migrationBuilder.AddCheckConstraint(
                name: "ck_advance_request_relation",
                table: "field_advance_requests",
                sql: "(relation_kind='Normal' AND supplement_of_request_id IS NULL) OR (relation_kind='Supplement' AND supplement_of_request_id IS NOT NULL AND supplement_of_request_id<>id AND source_request_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_advance_revision_basis",
                table: "field_advance_request_revisions",
                sql: "approval_basis_amount>=total AND approval_basis_amount<=9999999999999999.99 AND approval_basis_amount=trunc(approval_basis_amount,2) AND jsonb_typeof(approval_basis_json)='object' AND jsonb_typeof(approval_basis_json->'Items')='array' AND jsonb_typeof(approval_basis_json->'Amount')='number' AND (approval_basis_json->>'Amount')::numeric=approval_basis_amount AND approval_basis_json ?& ARRAY['Amount','Items']");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_evidence_created_by",
                table: "field_advance_evidence",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_evidence_workspace_id_created_by_person_id",
                table: "field_advance_evidence",
                columns: new[] { "workspace_id", "created_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_evidence_workspace_id_project_id_family_id_re~",
                table: "field_advance_evidence",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_evidence_locations_evidence_id",
                table: "field_advance_evidence_locations",
                column: "evidence_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_evidence_locations_workspace_id_project_id_fa~",
                table: "field_advance_evidence_locations",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_families_workspace_id_owner_person_id",
                table: "field_advance_families",
                columns: new[] { "workspace_id", "owner_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_families_workspace_id_project_id_id_pending_s~",
                table: "field_advance_families",
                columns: new[] { "workspace_id", "project_id", "id", "pending_supplement_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_created_by",
                table: "field_advance_installment_revisions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_installment_id",
                table: "field_advance_installment_revisions",
                column: "installment_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_installment_id_revision~",
                table: "field_advance_installment_revisions",
                columns: new[] { "installment_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_reviewed_by",
                table: "field_advance_installment_revisions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_workspace_id_project_id~",
                table: "field_advance_installment_revisions",
                columns: new[] { "workspace_id", "project_id", "family_id", "recipient_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installment_revisions_workspace_id_reviewed_b~",
                table: "field_advance_installment_revisions",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_installments_created_by",
                table: "field_advance_installments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_accounts_request_id",
                table: "field_advance_payment_accounts",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_accounts_workspace_id_project_id_fami~",
                table: "field_advance_payment_accounts",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_accounts_workspace_id_project_id_requ~",
                table: "field_advance_payment_accounts",
                columns: new[] { "workspace_id", "project_id", "request_id", "approved_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_attempts_installment_id",
                table: "field_advance_payment_attempts",
                column: "installment_id",
                unique: true,
                filter: "state IN ('Open','Unknown','Mismatch')");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_attempts_payer_id",
                table: "field_advance_payment_attempts",
                column: "payer_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_attempts_workspace_id_command_id",
                table: "field_advance_payment_attempts",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_attempts_workspace_id_payer_person_id",
                table: "field_advance_payment_attempts",
                columns: new[] { "workspace_id", "payer_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_attempts_workspace_id_project_id_fami~",
                table: "field_advance_payment_attempts",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_events_actor_id",
                table: "field_advance_payment_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_events_workspace_id_actor_person_id",
                table: "field_advance_payment_events",
                columns: new[] { "workspace_id", "actor_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_events_workspace_id_command_id",
                table: "field_advance_payment_events",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_events_workspace_id_project_id_family~",
                table: "field_advance_payment_events",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_created_by",
                table: "field_advance_payment_holds",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_workspace_id_created_by_person_~",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "created_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_workspace_id_released_by_person~",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "released_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_participations_created_by",
                table: "field_advance_payment_participations",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_participations_family_id_person_id_ro~",
                table: "field_advance_payment_participations",
                columns: new[] { "family_id", "person_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_participations_workspace_id_person_id",
                table: "field_advance_payment_participations",
                columns: new[] { "workspace_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_participations_workspace_id_project_i~",
                table: "field_advance_payment_participations",
                columns: new[] { "workspace_id", "project_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_proposed_by",
                table: "field_advance_payment_proposals",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_reviewed_by",
                table: "field_advance_payment_proposals",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_workspace_id_command_id",
                table: "field_advance_payment_proposals",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_workspace_id_project_id_fam~",
                table: "field_advance_payment_proposals",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_workspace_id_proposed_by_pe~",
                table: "field_advance_payment_proposals",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_proposals_workspace_id_reviewed_by_pe~",
                table: "field_advance_payment_proposals",
                columns: new[] { "workspace_id", "reviewed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_attempt_id",
                table: "field_advance_payment_results",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_installment_id",
                table: "field_advance_payment_results",
                column: "installment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_recorded_by",
                table: "field_advance_payment_results",
                column: "recorded_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_workspace_id_command_id",
                table: "field_advance_payment_results",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_workspace_id_project_id_famil~",
                table: "field_advance_payment_results",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_workspace_id_recipient_person~",
                table: "field_advance_payment_results",
                columns: new[] { "workspace_id", "recipient_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_results_workspace_id_recorded_by_pers~",
                table: "field_advance_payment_results",
                columns: new[] { "workspace_id", "recorded_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_reservation_entries_workspace_id_command_id_a~",
                table: "field_advance_reservation_entries",
                columns: new[] { "workspace_id", "command_id", "action", "installment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_reservation_entries_workspace_id_project_id_f~",
                table: "field_advance_reservation_entries",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_field_advance_requests_field_advance_families_workspace_id_~",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "family_id", "owner_person_id" },
                principalTable: "field_advance_families",
                principalColumns: new[] { "workspace_id", "project_id", "id", "owner_person_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_request_supplement_family",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "family_id", "supplement_of_request_id" },
                principalTable: "field_advance_requests",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(PaymentGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RemovePaymentGuards);
            migrationBuilder.DropForeignKey(
                name: "FK_field_advance_requests_field_advance_families_workspace_id_~",
                table: "field_advance_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_request_supplement_family",
                table: "field_advance_requests");

            migrationBuilder.DropTable(
                name: "field_advance_evidence_locations");

            migrationBuilder.DropTable(
                name: "field_advance_payment_accounts");

            migrationBuilder.DropTable(
                name: "field_advance_payment_events");

            migrationBuilder.DropTable(
                name: "field_advance_payment_holds");

            migrationBuilder.DropTable(
                name: "field_advance_payment_participations");

            migrationBuilder.DropTable(
                name: "field_advance_payment_proposals");

            migrationBuilder.DropTable(
                name: "field_advance_payment_results");

            migrationBuilder.DropTable(
                name: "field_advance_reservation_entries");

            migrationBuilder.DropTable(
                name: "field_advance_evidence");

            migrationBuilder.DropTable(
                name: "field_advance_payment_attempts");

            migrationBuilder.DropTable(
                name: "field_advance_installment_revisions");

            migrationBuilder.DropTable(
                name: "field_advance_installments");

            migrationBuilder.DropTable(
                name: "field_advance_families");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_field_advance_requests_workspace_id_project_id_family_id_id",
                table: "field_advance_requests");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_requests_family_id",
                table: "field_advance_requests");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_requests_workspace_id_project_id_family_id_ow~",
                table: "field_advance_requests");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_requests_workspace_id_project_id_family_id_su~",
                table: "field_advance_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_advance_request_relation",
                table: "field_advance_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_advance_revision_basis",
                table: "field_advance_request_revisions");

            migrationBuilder.DropColumn(
                name: "family_id",
                table: "field_advance_requests");

            migrationBuilder.DropColumn(
                name: "relation_kind",
                table: "field_advance_requests");

            migrationBuilder.DropColumn(
                name: "supplement_of_request_id",
                table: "field_advance_requests");

            migrationBuilder.DropColumn(
                name: "approval_basis_amount",
                table: "field_advance_request_revisions");

            migrationBuilder.DropColumn(
                name: "approval_basis_json",
                table: "field_advance_request_revisions");
        }
    }
}
