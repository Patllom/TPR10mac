using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentIncidentProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pay_AdvancePaymentHold_6",
                table: "field_advance_payment_holds");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds",
                newName: "IX_field_advance_payment_holds_workspace_id_project_id_family~1");

            migrationBuilder.AddColumn<Guid>(
                name: "source_proposal_id",
                table: "field_advance_payment_holds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_field_advance_payment_proposals_workspace_id_project_id_fam~",
                table: "field_advance_payment_proposals",
                columns: new[] { "workspace_id", "project_id", "family_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_source_proposal_id",
                table: "field_advance_payment_holds",
                column: "source_proposal_id",
                unique: true,
                filter: "source_proposal_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "source_proposal_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_pay_AdvancePaymentHold_6",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "source_proposal_id" },
                principalTable: "field_advance_payment_proposals",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_pay_AdvancePaymentHold_7",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" },
                principalTable: "field_advance_installments",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_payment_holds, field_advance_payment_incident_resolutions,
                    field_advance_payment_incident_recoveries, field_advance_settlement_excess_cases,
                    field_advance_settlement_rounds, field_advance_settlement_case_payments,
                    field_advance_settlement_case_closures, field_advance_settlement_adjustment_cases,
                    field_advance_refund_corrections IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_payment_holds WHERE source_proposal_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_incident_resolutions)
                        OR EXISTS (SELECT 1 FROM field_advance_payment_incident_recoveries)
                        -- Preserve the current schema when earlier guarded migrations would reject.
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_excess_cases)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_rounds WHERE round_kind='Adjustment')
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_payments)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_closures)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_adjustment_cases WHERE root_refund_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_refund_corrections WHERE supersedes_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Incident provenance prevents destructive downgrade' USING ERRCODE='23514';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_pay_AdvancePaymentHold_6",
                table: "field_advance_payment_holds");

            migrationBuilder.DropForeignKey(
                name: "fk_pay_AdvancePaymentHold_7",
                table: "field_advance_payment_holds");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_field_advance_payment_proposals_workspace_id_project_id_fam~",
                table: "field_advance_payment_proposals");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_payment_holds_source_proposal_id",
                table: "field_advance_payment_holds");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_payment_holds_workspace_id_project_id_family_~",
                table: "field_advance_payment_holds");

            migrationBuilder.DropColumn(
                name: "source_proposal_id",
                table: "field_advance_payment_holds");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_payment_holds_workspace_id_project_id_family~1",
                table: "field_advance_payment_holds",
                newName: "IX_field_advance_payment_holds_workspace_id_project_id_family_~");

            migrationBuilder.AddForeignKey(
                name: "fk_pay_AdvancePaymentHold_6",
                table: "field_advance_payment_holds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "installment_id" },
                principalTable: "field_advance_installments",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
