using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrectionRecoveryDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "recovery_day_id",
                table: "correction_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_correction_revisions_recovery_day_id",
                table: "correction_revisions",
                column: "recovery_day_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_correction_recovery_baseline",
                table: "correction_revisions",
                sql: "recovery_day_id IS NULL OR baseline_day_version IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_correction_revisions_attendance_days_recovery_day_id",
                table: "correction_revisions",
                column: "recovery_day_id",
                principalTable: "attendance_days",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(AdjustmentGuard(true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Wait for in-flight inserts before taking the history preflight snapshot;
            // retain the lock until the migration transaction commits or rolls back.
            migrationBuilder.Sql("LOCK TABLE correction_revisions IN ACCESS EXCLUSIVE MODE;");
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM correction_revisions WHERE recovery_day_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove retained correction recovery history';
                    END IF;
                END $$;
                """);
            migrationBuilder.Sql(AdjustmentGuard(false));
            migrationBuilder.DropForeignKey(
                name: "FK_correction_revisions_attendance_days_recovery_day_id",
                table: "correction_revisions");

            migrationBuilder.DropIndex(
                name: "IX_correction_revisions_recovery_day_id",
                table: "correction_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_correction_recovery_baseline",
                table: "correction_revisions");

            migrationBuilder.DropColumn(
                name: "recovery_day_id",
                table: "correction_revisions");
        }

        // Frozen SQL for this migration. Request identity and earlier revisions remain immutable.
        private static string AdjustmentGuard(bool recovery) => """
            CREATE OR REPLACE FUNCTION correction_validate_adjustment() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM correction_requests r
                    JOIN correction_revisions v ON v.request_id=r.id AND v.revision=NEW.revision
                    JOIN correction_route_snapshots route ON route.request_id=r.id AND route.revision=v.revision
                    JOIN correction_approval_actions s ON s.request_id=r.id AND s.revision=v.revision AND s.step='Supervisor' AND s.outcome='Approved'
                    JOIN correction_approval_actions h ON h.request_id=r.id AND h.revision=v.revision AND h.step='Hr' AND h.outcome='Approved'
                    JOIN attendance_days d ON d.id=NEW.day_id
                    WHERE r.id=NEW.request_id AND r.state='Approved' AND r.current_revision=v.revision
                      AND s.actor_id=route.supervisor_id AND h.actor_id=ANY(route.hr_candidate_ids)
                      AND s.actor_id<>h.actor_id AND s.actor_id<>r.employee_id AND h.actor_id<>r.employee_id
                      AND s.occurred_at_utc<=h.occurred_at_utc AND h.occurred_at_utc<=NEW.applied_at_utc
                      AND v.baseline_day_version IS NOT DISTINCT FROM NEW.before_version
                      AND (__TARGET__)
                      AND ROW(d.employee_id,d.membership_id,d.workspace_id,d.department_id)
                          IS NOT DISTINCT FROM ROW(r.employee_id,r.membership_id,r.workspace_id,r.department_id)
                      AND (__SNAPSHOT__)
                      AND ROW(v.work_date,v.in_utc,v.out_utc) IS NOT DISTINCT FROM ROW(NEW.after_work_date,NEW.after_in_utc,NEW.after_out_utc)
                      AND ROW(d.work_date,d.effective_in_utc,d.effective_out_utc,d.version)
                          IS NOT DISTINCT FROM ROW(NEW.after_work_date,NEW.after_in_utc,NEW.after_out_utc,NEW.after_version)
                      AND d.written_transaction_id=txid_current()
                ) THEN
                    RAISE EXCEPTION 'Adjustment requires exact proposal, distinct approvals and atomic day change' USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END $$;
            """.Replace("__TARGET__", recovery
                ? "(r.day_id=NEW.day_id AND v.recovery_day_id IS NULL AND NEW.before_version IS NOT NULL) OR (r.day_id IS NULL AND v.recovery_day_id IS NULL AND NEW.before_version IS NULL) OR (r.day_id IS NULL AND v.recovery_day_id=NEW.day_id AND NEW.before_version IS NOT NULL AND NEW.before_work_date=r.original_work_date)"
                : "(r.day_id=NEW.day_id AND NEW.before_version IS NOT NULL) OR (r.day_id IS NULL AND NEW.before_version IS NULL)")
            .Replace("__SNAPSHOT__", recovery
                ? "(r.day_id IS NULL AND v.recovery_day_id IS NOT NULL) OR d.snapshot_at_utc IS NOT DISTINCT FROM r.snapshot_at_utc"
                : "d.snapshot_at_utc IS NOT DISTINCT FROM r.snapshot_at_utc");
    }
}
