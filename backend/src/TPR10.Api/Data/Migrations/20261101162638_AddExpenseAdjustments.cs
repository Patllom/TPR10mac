using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION advance_expense_zero_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW.quantity=0 OR NEW.lump_sum=0) AND NOT EXISTS (
                        SELECT 1 FROM field_advance_settlement_revisions v JOIN field_advance_settlement_rounds r ON r.id=v.round_id
                        WHERE v.id=NEW.revision_id AND r.round_kind='Adjustment' AND NEW.source_line_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Only sourced adjustment lines may be zero' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER expense_zero_history BEFORE INSERT OR UPDATE ON field_advance_settlement_lines
                    FOR EACH ROW EXECUTE FUNCTION advance_expense_zero_guard();
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_6",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_7",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementRound_6",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementLine_line",
                table: "field_advance_settlement_lines");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_project_id_fam~",
                table: "field_advance_settlement_rounds",
                newName: "IX_field_advance_settlement_rounds_workspace_id_project_id_fa~1");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~2",
                table: "field_advance_settlement_expense_adjustments",
                newName: "IX_field_advance_settlement_expense_adjustments_workspace_id_~3");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~1",
                table: "field_advance_settlement_expense_adjustments",
                newName: "IX_field_advance_settlement_expense_adjustments_workspace_id_~2");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2",
                table: "field_advance_settlement_adjustment_cases",
                newName: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~3");

            migrationBuilder.AddColumn<Guid>(
                name: "adjustment_case_id",
                table: "field_advance_settlement_rounds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "round_kind",
                table: "field_advance_settlement_rounds",
                type: "text",
                nullable: false,
                defaultValue: "Ordinary");

            migrationBuilder.AddColumn<Guid>(
                name: "adjustment_case_id",
                table: "field_advance_settlement_expense_adjustments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "root_expense_round_id",
                table: "field_advance_settlement_adjustment_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_project_id_fam~",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" });

            // Populate only the newly added discriminator; preserve every historical fact.
            migrationBuilder.Sql("""
                ALTER TABLE field_advance_settlement_rounds DISABLE TRIGGER settlement_accepted_history;
                UPDATE field_advance_settlement_rounds SET round_kind='Adjustment' WHERE source_round_id IS NOT NULL;
                ALTER TABLE field_advance_settlement_rounds ENABLE TRIGGER settlement_accepted_history;
                """);
            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementRound_kind",
                table: "field_advance_settlement_rounds",
                sql: "(round_kind='Ordinary' AND source_round_id IS NULL AND adjustment_case_id IS NULL) OR (round_kind='Adjustment' AND source_round_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementLine_line",
                table: "field_advance_settlement_lines",
                sql: "(quantity IS NOT NULL AND rate IS NOT NULL AND lump_sum IS NULL AND quantity>=0 AND rate>0 AND amount=round(quantity*rate,2)) OR (quantity IS NULL AND rate IS NULL AND lump_sum IS NOT NULL AND lump_sum>=0 AND amount=lump_sum)");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~1",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_expense_round_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases",
                sql: "(root_refund_id IS NULL AND root_expense_round_id IS NULL AND baseline_version IS NULL AND baseline_amount IS NULL) OR (baseline_version IS NOT NULL AND baseline_version>0 AND baseline_amount IS NOT NULL AND ((root_refund_id IS NOT NULL AND root_expense_round_id IS NULL AND kind='RefundCorrection') OR (root_expense_round_id IS NOT NULL AND root_refund_id IS NULL AND kind='ExpenseAdjustment')))");

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_expense_round_id" },
                principalTable: "field_advance_settlement_rounds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementAdjustmentCase_9",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_refund_id" },
                principalTable: "field_advance_refunds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_6",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" },
                principalTable: "field_advance_settlement_adjustment_cases",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_7",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id" },
                principalTable: "field_advance_settlement_revisions",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_8",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_revision_id" },
                principalTable: "field_advance_settlement_revisions",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementRound_6",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "adjustment_case_id" },
                principalTable: "field_advance_settlement_adjustment_cases",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementRound_7",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_round_id" },
                principalTable: "field_advance_settlement_rounds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE field_advance_settlement_rounds, field_advance_settlement_adjustment_cases,
                    field_advance_refund_corrections, field_advance_settlement_case_payments,
                    field_advance_settlement_case_closures IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM field_advance_settlement_rounds WHERE round_kind='Adjustment')
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_payments)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_case_closures)
                        OR EXISTS (SELECT 1 FROM field_advance_settlement_adjustment_cases WHERE root_refund_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM field_advance_refund_corrections WHERE supersedes_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Expense adjustment history prevents downgrade' USING ERRCODE='23514';
                    END IF;
                END $$;
                DROP TRIGGER expense_zero_history ON field_advance_settlement_lines;
                DROP FUNCTION advance_expense_zero_guard();
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementAdjustmentCase_9",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_6",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_7",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_8",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementRound_6",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropForeignKey(
                name: "fk_st_SettlementRound_7",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_project_id_fam~",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementRound_kind",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementLine_line",
                table: "field_advance_settlement_lines");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~1",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.DropColumn(
                name: "adjustment_case_id",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropColumn(
                name: "round_kind",
                table: "field_advance_settlement_rounds");

            migrationBuilder.DropColumn(
                name: "adjustment_case_id",
                table: "field_advance_settlement_expense_adjustments");

            migrationBuilder.DropColumn(
                name: "root_expense_round_id",
                table: "field_advance_settlement_adjustment_cases");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_rounds_workspace_id_project_id_fa~1",
                table: "field_advance_settlement_rounds",
                newName: "IX_field_advance_settlement_rounds_workspace_id_project_id_fam~");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~2",
                table: "field_advance_settlement_expense_adjustments",
                newName: "IX_field_advance_settlement_expense_adjustments_workspace_id_~1");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_expense_adjustments_workspace_id_~3",
                table: "field_advance_settlement_expense_adjustments",
                newName: "IX_field_advance_settlement_expense_adjustments_workspace_id_~2");

            migrationBuilder.RenameIndex(
                name: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~3",
                table: "field_advance_settlement_adjustment_cases",
                newName: "IX_field_advance_settlement_adjustment_cases_workspace_id_pro~2");

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementLine_line",
                table: "field_advance_settlement_lines",
                sql: "(quantity IS NOT NULL AND rate IS NOT NULL AND lump_sum IS NULL AND quantity>0 AND rate>0 AND amount=round(quantity*rate,2)) OR (quantity IS NULL AND rate IS NULL AND lump_sum IS NOT NULL AND lump_sum>0 AND amount=lump_sum)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_SettlementAdjustmentCase_baseline",
                table: "field_advance_settlement_adjustment_cases",
                sql: "(root_refund_id IS NULL AND baseline_version IS NULL AND baseline_amount IS NULL) OR (root_refund_id IS NOT NULL AND baseline_version IS NOT NULL AND baseline_version>0 AND baseline_amount IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementAdjustmentCase_8",
                table: "field_advance_settlement_adjustment_cases",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "root_refund_id" },
                principalTable: "field_advance_refunds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_6",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "revision_id" },
                principalTable: "field_advance_settlement_revisions",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementExpenseAdjustment_7",
                table: "field_advance_settlement_expense_adjustments",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_revision_id" },
                principalTable: "field_advance_settlement_revisions",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_st_SettlementRound_6",
                table: "field_advance_settlement_rounds",
                columns: new[] { "workspace_id", "project_id", "family_id", "request_id", "source_round_id" },
                principalTable: "field_advance_settlement_rounds",
                principalColumns: new[] { "workspace_id", "project_id", "family_id", "request_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
