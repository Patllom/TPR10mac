using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentPaymentBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pr~3",
                table: "field_advance_payment_incident_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_hold",
                table: "field_advance_payment_holds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_account_balance",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installment_state",
                table: "field_advance_installments");

            migrationBuilder.AddColumn<decimal>(
                name: "activated_shortfall",
                table: "field_advance_payment_accounts",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "incident_reservation_usage",
                table: "field_advance_payment_accounts",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "effective_usage",
                table: "field_advance_payment_accounts",
                type: "numeric",
                nullable: false,
                computedColumnSql: "paid + incident_reservation_usage - activated_shortfall",
                stored: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_field_advance_payment_incident_resolutions_workspace_id_pr~1",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id", "id" });

            migrationBuilder.CreateTable(
                name: "field_advance_incident_payment_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_incident_payment_adjustments", x => x.id);
                    table.CheckConstraint("ck_incident_payment_adjustments_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_incident_payment_adjustments_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_0",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_1",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_IncidentPaymentAdjustment_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id, x.attempt_id, x.incident_id },
                        principalTable: "field_advance_payment_incident_resolutions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_payment_shortfall_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_payment_shortfall_claims", x => x.id);
                    table.CheckConstraint("ck_payment_shortfall_claims_amount", "amount>=0 AND amount<=9999999999999999.99 AND amount=trunc(amount,2)");
                    table.CheckConstraint("ck_payment_shortfall_claims_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_shortfall_claim", "amount>0 AND installment_id<>source_installment_id AND state IN ('Reserved','Consumed','Settled','Cancelled')");
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_0",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_1",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_2",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id },
                        principalTable: "field_advance_families",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_3",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_4",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.installment_id },
                        principalTable: "field_advance_installments",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pay_PaymentShortfallClaim_5",
                        columns: x => new { x.workspace_id, x.project_id, x.family_id, x.request_id, x.source_installment_id, x.source_attempt_id, x.incident_id },
                        principalTable: "field_advance_payment_incident_resolutions",
                        principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_hold",
                table: "field_advance_payment_holds",
                sql: "length(btrim(code))>0 AND length(btrim(reason))>0 AND (installment_id IS NULL OR request_id IS NOT NULL) AND (code<>'PaymentMismatch' OR (request_id IS NULL AND installment_id IS NULL)) AND ((released_at_utc IS NULL AND released_by_person_id IS NULL) OR (released_at_utc IS NOT NULL AND isfinite(released_at_utc) AND released_by_person_id IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_account_balance",
                table: "field_advance_payment_accounts",
                sql: "effective_usage+reserved<=spendable AND spendable<=approved AND (paid=0 OR ever_paid) AND ((approved_revision_id IS NULL AND approved=0) OR (approved_revision_id IS NOT NULL AND approved>0))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_accounts_activated_shortfall",
                table: "field_advance_payment_accounts",
                sql: "activated_shortfall>=0 AND activated_shortfall<=9999999999999999.99 AND activated_shortfall=trunc(activated_shortfall,2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_accounts_effective_usage",
                table: "field_advance_payment_accounts",
                sql: "effective_usage>=0 AND effective_usage<=9999999999999999.99 AND effective_usage=trunc(effective_usage,2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_accounts_incident_reservation_usage",
                table: "field_advance_payment_accounts",
                sql: "incident_reservation_usage>=0 AND incident_reservation_usage<=9999999999999999.99 AND incident_reservation_usage=trunc(incident_reservation_usage,2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installment_state",
                table: "field_advance_installments",
                sql: "state IN ('Draft','PendingReview','Returned','Reviewed','Paying','OutcomePending','CancelPending','Cancelled','Paid','IncidentResolved')");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_incident_payment_adjustments_attempt_id",
                table: "field_advance_incident_payment_adjustments",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_incident_payment_adjustments_incident_id",
                table: "field_advance_incident_payment_adjustments",
                column: "incident_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_incident_payment_adjustments_installment_id",
                table: "field_advance_incident_payment_adjustments",
                column: "installment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_incident_payment_adjustments_workspace_id_com~",
                table: "field_advance_incident_payment_adjustments",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_incident_payment_adjustments_workspace_id_pro~",
                table: "field_advance_incident_payment_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_shortfall_claims_command_id",
                table: "field_advance_payment_shortfall_claims",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_shortfall_claims_installment_id",
                table: "field_advance_payment_shortfall_claims",
                column: "installment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_shortfall_claims_workspace_id_command~",
                table: "field_advance_payment_shortfall_claims",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_shortfall_claims_workspace_id_projec~1",
                table: "field_advance_payment_shortfall_claims",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_installment_id", "source_attempt_id", "incident_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_shortfall_claims_workspace_id_project~",
                table: "field_advance_payment_shortfall_claims",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" });
            migrationBuilder.Sql(BridgeGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_requests, field_advance_families, field_advance_policy_revisions, field_advance_policy_reviews, field_advance_incident_payment_adjustments, field_advance_payment_shortfall_claims, field_advance_payment_accounts, field_advance_installments, field_advance_payment_holds, field_advance_payment_incident_resolutions,
                    field_advance_payment_incident_recoveries, field_advance_settlement_excess_cases,
                    field_advance_settlement_rounds, field_advance_settlement_case_payments,
                    field_advance_settlement_case_closures, field_advance_settlement_adjustment_cases,
                    field_advance_refund_corrections IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    -- Earlier migrations commit independently. Reject before removing the
                    -- account projection if a lower foundation/history guard would reject.
                    IF EXISTS (SELECT 1 FROM field_advance_requests)
                        OR EXISTS (SELECT 1 FROM field_advance_families)
                        OR EXISTS (SELECT 1 FROM field_advance_policy_revisions WHERE correction_days IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_policy_reviews WHERE reviewed_content_json->'Content'->>'CorrectionDays' IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_incident_payment_adjustments)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_shortfall_claims)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_accounts WHERE incident_reservation_usage<>0 OR activated_shortfall<>0)
                        OR EXISTS (SELECT 1 FROM field_advance_installments WHERE state='IncidentResolved')
                        OR EXISTS (SELECT 1 FROM field_advance_payment_holds WHERE code='PaymentMismatch' AND released_at_utc IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_holds WHERE source_proposal_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_incident_resolutions)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_incident_recoveries)
                        -- Preserve the current schema when earlier guarded migrations would reject.
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_excess_cases)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_rounds WHERE round_kind='Adjustment')
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_payments)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_closures)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_adjustment_cases WHERE root_refund_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_refund_corrections WHERE supersedes_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Incident payment history prevents destructive downgrade' USING ERRCODE='23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql("""
                DROP TRIGGER incident_capacity_account ON field_advance_payment_accounts;
                DROP TRIGGER incident_capacity_adjustment ON field_advance_incident_payment_adjustments;
                DROP TRIGGER incident_capacity_claim ON field_advance_payment_shortfall_claims;
                DROP TRIGGER incident_adjustment_immutable ON field_advance_incident_payment_adjustments;
                DROP TRIGGER incident_claim_history ON field_advance_payment_shortfall_claims;
                DROP FUNCTION advance_incident_capacity();
                DROP FUNCTION advance_incident_adjustment_immutable();
                DROP FUNCTION advance_incident_claim_history();
                """);
            migrationBuilder.DropTable(
                name: "field_advance_incident_payment_adjustments");

            migrationBuilder.DropTable(
                name: "field_advance_payment_shortfall_claims");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_field_advance_payment_incident_resolutions_workspace_id_pr~1",
                table: "field_advance_payment_incident_resolutions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_hold",
                table: "field_advance_payment_holds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_account_balance",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_accounts_activated_shortfall",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_accounts_effective_usage",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_accounts_incident_reservation_usage",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installment_state",
                table: "field_advance_installments");

            migrationBuilder.DropColumn(
                name: "effective_usage",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropColumn(
                name: "activated_shortfall",
                table: "field_advance_payment_accounts");

            migrationBuilder.DropColumn(
                name: "incident_reservation_usage",
                table: "field_advance_payment_accounts");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_incident_resolutions_workspace_id_pr~3",
                table: "field_advance_payment_incident_resolutions",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id", "attempt_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_hold",
                table: "field_advance_payment_holds",
                sql: "length(btrim(code))>0 AND length(btrim(reason))>0 AND (installment_id IS NULL OR request_id IS NOT NULL) AND (code<>'PaymentMismatch' OR (request_id IS NULL AND installment_id IS NULL AND released_at_utc IS NULL)) AND ((released_at_utc IS NULL AND released_by_person_id IS NULL) OR (released_at_utc IS NOT NULL AND isfinite(released_at_utc) AND released_by_person_id IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_account_balance",
                table: "field_advance_payment_accounts",
                sql: "paid+reserved<=spendable AND spendable<=approved AND (paid=0 OR ever_paid) AND ((approved_revision_id IS NULL AND approved=0) OR (approved_revision_id IS NOT NULL AND approved>0))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installment_state",
                table: "field_advance_installments",
                sql: "state IN ('Draft','PendingReview','Returned','Reviewed','Paying','OutcomePending','CancelPending','Cancelled','Paid')");
        }
    }
}
