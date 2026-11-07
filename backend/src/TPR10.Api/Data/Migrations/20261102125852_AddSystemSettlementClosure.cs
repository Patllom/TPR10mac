using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemSettlementClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_st_ClosureRecord_review",
                table: "field_advance_closure_records");

            migrationBuilder.AlterColumn<Guid>(
                name: "proposed_by_person_id",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "proposed_by",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "command_id",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "actor_kind",
                table: "field_advance_closure_records",
                type: "text",
                nullable: false,
                defaultValue: "Human");

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_ClosureRecord_review",
                table: "field_advance_closure_records",
                sql: "(actor_kind='Human' AND command_id IS NOT NULL AND proposed_by IS NOT NULL AND proposed_by_person_id IS NOT NULL AND (((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL))) OR (actor_kind='System' AND command_id IS NULL AND proposed_by IS NULL AND proposed_by_person_id IS NULL AND reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL AND accepted_at_utc IS NOT NULL AND state='Accepted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN PERFORM pg_advisory_xact_lock(7241002); END $$;
                LOCK TABLE field_advance_requests, field_advance_families, field_advance_policy_revisions, field_advance_policy_reviews, field_advance_incident_payment_adjustments, field_advance_payment_shortfall_claims, field_advance_payment_accounts, field_advance_installments, field_advance_payment_holds, field_advance_payment_incident_resolutions,
                    field_advance_payment_incident_recoveries, field_advance_settlement_excess_cases,
                    field_advance_settlement_rounds, field_advance_settlement_case_payments,
                    field_advance_settlement_case_closures, field_advance_settlement_adjustment_cases,
                    field_advance_refund_corrections, field_advance_closure_records IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    -- Each migration can commit independently. Mirror the predecessor history
                    -- preflight before any Task13 DDL; even a one-step downgrade is
                    -- restricted when lower guarded history exists.
                    IF EXISTS (SELECT 1 FROM field_advance_closure_records WHERE actor_kind='System') THEN
                        RAISE EXCEPTION 'system-closure-downgrade-blocked' USING ERRCODE='23514';
                    END IF;
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
                        RAISE EXCEPTION 'supplement-order-downgrade-blocked' USING ERRCODE='23514';
                    END IF;
                END $$;

                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_st_ClosureRecord_review",
                table: "field_advance_closure_records");

            migrationBuilder.DropColumn(
                name: "actor_kind",
                table: "field_advance_closure_records");

            migrationBuilder.AlterColumn<Guid>(
                name: "proposed_by_person_id",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "proposed_by",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "command_id",
                table: "field_advance_closure_records",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_st_ClosureRecord_review",
                table: "field_advance_closure_records",
                sql: "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)");
        }
    }
}
