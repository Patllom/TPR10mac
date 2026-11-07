using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplementClosureOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LegacyPreflight);
            migrationBuilder.AddColumn<long>(
                name: "family_version_at_creation",
                table: "field_advance_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "legacy_family_version_baseline",
                table: "field_advance_requests",
                type: "bigint",
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE field_advance_requests r SET legacy_family_version_baseline=f.version
                FROM field_advance_families f WHERE r.family_id=f.id AND r.relation_kind='Supplement';
                """);
            migrationBuilder.AddCheckConstraint("ck_supplement_order_provenance", "field_advance_requests", "(relation_kind='Normal' AND num_nonnulls(family_version_at_creation,legacy_family_version_baseline)=0) OR (relation_kind='Supplement' AND num_nonnulls(family_version_at_creation,legacy_family_version_baseline)=1 AND COALESCE(family_version_at_creation,legacy_family_version_baseline)>0)");
            migrationBuilder.Sql(OrderGuards);
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

                DROP TRIGGER supplement_order_proof ON field_advance_requests;
                DROP TRIGGER supplement_order_commit ON field_advance_requests;
                DROP TRIGGER supplement_closure_commit ON field_advance_closure_records;
                DROP FUNCTION supplement_closure_commit();
                DROP TRIGGER supplement_closure_order ON field_advance_closure_records;
                DROP FUNCTION supplement_order_proof();
                DROP FUNCTION supplement_order_commit();
                DROP FUNCTION supplement_closure_order();
                """);
            migrationBuilder.DropCheckConstraint("ck_supplement_order_provenance", "field_advance_requests");
            migrationBuilder.DropColumn(
                name: "family_version_at_creation",
                table: "field_advance_requests");

            migrationBuilder.DropColumn(
                name: "legacy_family_version_baseline",
                table: "field_advance_requests");
        }
    }
}
