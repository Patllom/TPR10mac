using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_day_date",
                table: "attendance_days");

            migrationBuilder.CreateTable(
                name: "correction_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    day_id = table.Column<Guid>(type: "uuid", nullable: true),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_anchor_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "Draft"),
                    current_revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_requests", x => x.id);
                    table.CheckConstraint("ck_correction_deadline", "isfinite(original_work_date) AND isfinite(snapshot_at_utc) AND isfinite(deadline_anchor_utc) AND isfinite(deadline_utc) AND deadline_anchor_utc=((original_work_date+8)::timestamp AT TIME ZONE 'Asia/Bangkok')");
                    table.CheckConstraint("ck_correction_state", "state IN ('Draft','PendingSupervisor','PendingHr','Returned','Approved','Expired')");
                    table.CheckConstraint("ck_correction_version", "version>=1 AND current_revision>=0");
                    table.ForeignKey(
                        name: "FK_correction_requests_attendance_days_day_id_employee_id",
                        columns: x => new { x.day_id, x.employee_id },
                        principalTable: "attendance_days",
                        principalColumns: new[] { "id", "employee_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_requests_employee_memberships_membership_id_empl~",
                        columns: x => new { x.membership_id, x.employee_id, x.workspace_id, x.department_id },
                        principalTable: "employee_memberships",
                        principalColumns: new[] { "id", "user_id", "workspace_id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_command_receipts",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_command_receipts", x => new { x.actor_id, x.command_id });
                    table.CheckConstraint("ck_correction_receipt", "command_id<>'00000000-0000-0000-0000-000000000000'::uuid AND payload_sha256 ~ '^[a-f0-9]{64}$' AND status IN (200,201) AND isfinite(created_at_utc) AND length(action)>0 AND jsonb_typeof(result_json)='object' AND octet_length(result_json::text)<=65536");
                    table.ForeignKey(
                        name: "FK_correction_command_receipts_correction_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "correction_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_command_receipts_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_revisions",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    baseline_day_version = table.Column<long>(type: "bigint", nullable: true),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    in_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    out_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    in_workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    out_workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    out_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    out_site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_revisions", x => new { x.request_id, x.revision });
                    table.CheckConstraint("ck_correction_proposal_site", "(out_utc IS NULL AND out_workspace_id IS NULL AND out_project_id IS NULL AND out_site_id IS NULL) OR (out_utc IS NOT NULL AND out_workspace_id IS NOT NULL AND out_project_id IS NOT NULL AND out_site_id IS NOT NULL)");
                    table.CheckConstraint("ck_correction_proposal_time", "isfinite(work_date) AND isfinite(in_utc) AND isfinite(created_at_utc) AND work_date=(in_utc AT TIME ZONE 'Asia/Bangkok')::date AND in_utc<=created_at_utc AND (out_utc IS NULL OR (isfinite(out_utc) AND out_utc>in_utc AND out_utc<=created_at_utc))");
                    table.CheckConstraint("ck_correction_reason", "length(btrim(reason)) BETWEEN 1 AND 2000 AND reason=btrim(reason)");
                    table.CheckConstraint("ck_correction_revision", "revision>=1 AND (baseline_day_version IS NULL OR baseline_day_version>=1)");
                    table.ForeignKey(
                        name: "FK_correction_revisions_correction_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "correction_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_revisions_sites_in_workspace_id_in_project_id_in~",
                        columns: x => new { x.in_workspace_id, x.in_project_id, x.in_site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_revisions_sites_out_workspace_id_out_project_id_~",
                        columns: x => new { x.out_workspace_id, x.out_project_id, x.out_site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    day_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before_version = table.Column<long>(type: "bigint", nullable: true),
                    before_work_date = table.Column<DateOnly>(type: "date", nullable: true),
                    before_in_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    before_out_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    after_version = table.Column<long>(type: "bigint", nullable: false),
                    after_work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    after_in_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    after_out_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_adjustments", x => x.id);
                    table.CheckConstraint("ck_adjustment_time", "isfinite(after_work_date) AND isfinite(after_in_utc) AND isfinite(applied_at_utc) AND (after_out_utc IS NULL OR (isfinite(after_out_utc) AND after_out_utc>after_in_utc))");
                    table.CheckConstraint("ck_adjustment_version", "(before_version IS NULL AND before_work_date IS NULL AND before_in_utc IS NULL AND before_out_utc IS NULL AND after_version=1) OR (before_version IS NOT NULL AND before_version>=1 AND before_work_date IS NOT NULL AND before_in_utc IS NOT NULL AND after_version=before_version+1)");
                    table.ForeignKey(
                        name: "FK_attendance_adjustments_attendance_days_day_id",
                        column: x => x.day_id,
                        principalTable: "attendance_days",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_adjustments_correction_revisions_request_id_revi~",
                        columns: x => new { x.request_id, x.revision },
                        principalTable: "correction_revisions",
                        principalColumns: new[] { "request_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_route_snapshots",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    supervisor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hr_candidate_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    membership_version = table.Column<long>(type: "bigint", nullable: false),
                    reporting_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_route_snapshots", x => new { x.request_id, x.revision });
                    table.CheckConstraint("ck_correction_route", "membership_version>=1 AND reporting_version>=1 AND cardinality(hr_candidate_ids)>0 AND isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_correction_route_snapshots_correction_revisions_request_id_~",
                        columns: x => new { x.request_id, x.revision },
                        principalTable: "correction_revisions",
                        principalColumns: new[] { "request_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_route_snapshots_users_supervisor_id",
                        column: x => x.supervisor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "correction_approval_actions",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    step = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_approval_actions", x => new { x.request_id, x.revision, x.step });
                    table.CheckConstraint("ck_correction_approval", "step IN ('Supervisor','Hr') AND outcome IN ('Approved','Returned') AND isfinite(occurred_at_utc)");
                    table.CheckConstraint("ck_correction_return_reason", "outcome<>'Returned' OR (reason IS NOT NULL AND length(btrim(reason)) BETWEEN 1 AND 2000 AND reason=btrim(reason))");
                    table.ForeignKey(
                        name: "FK_correction_approval_actions_correction_route_snapshots_requ~",
                        columns: x => new { x.request_id, x.revision },
                        principalTable: "correction_route_snapshots",
                        principalColumns: new[] { "request_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_correction_approval_actions_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_day_date",
                table: "attendance_days",
                sql: "work_date=(effective_in_utc AT TIME ZONE 'Asia/Bangkok')::date");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_adjustments_day_id_after_version",
                table: "attendance_adjustments",
                columns: new[] { "day_id", "after_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_adjustments_request_id_revision",
                table: "attendance_adjustments",
                columns: new[] { "request_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_correction_approval_actions_actor_id",
                table: "correction_approval_actions",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_correction_command_receipts_request_id",
                table: "correction_command_receipts",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_correction_requests_day_id_employee_id",
                table: "correction_requests",
                columns: new[] { "day_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "IX_correction_requests_employee_id_original_work_date",
                table: "correction_requests",
                columns: new[] { "employee_id", "original_work_date" },
                unique: true,
                filter: "state NOT IN ('Approved','Expired')");

            migrationBuilder.CreateIndex(
                name: "IX_correction_requests_membership_id_employee_id_workspace_id_~",
                table: "correction_requests",
                columns: new[] { "membership_id", "employee_id", "workspace_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_correction_revisions_in_workspace_id_in_project_id_in_site_~",
                table: "correction_revisions",
                columns: new[] { "in_workspace_id", "in_project_id", "in_site_id" });

            migrationBuilder.CreateIndex(
                name: "IX_correction_revisions_out_workspace_id_out_project_id_out_si~",
                table: "correction_revisions",
                columns: new[] { "out_workspace_id", "out_project_id", "out_site_id" });

            migrationBuilder.CreateIndex(
                name: "IX_correction_route_snapshots_supervisor_id",
                table: "correction_route_snapshots",
                column: "supervisor_id");
            migrationBuilder.AddColumn<long>(name: "written_transaction_id", table: "attendance_days", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "written_transaction_id", table: "attendance_events", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(name: "written_transaction_id", table: "attendance_adjustments", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.Sql(CorrectionGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CorrectionDownPreflight);
            migrationBuilder.DropColumn(name: "written_transaction_id", table: "attendance_days");
            migrationBuilder.DropColumn(name: "written_transaction_id", table: "attendance_events");
            migrationBuilder.DropTable(
                name: "attendance_adjustments");

            migrationBuilder.DropTable(
                name: "correction_approval_actions");

            migrationBuilder.DropTable(
                name: "correction_command_receipts");

            migrationBuilder.DropTable(
                name: "correction_route_snapshots");

            migrationBuilder.DropTable(
                name: "correction_revisions");

            migrationBuilder.DropTable(
                name: "correction_requests");

            migrationBuilder.Sql("DROP FUNCTION correction_validate_adjustment(), correction_revision_guard(), correction_preserve_request(), correction_immutable(), correction_route_guard(), correction_request_completion();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attendance_day_date",
                table: "attendance_days");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attendance_day_date",
                table: "attendance_days",
                sql: "work_date=(snapshot_at_utc AT TIME ZONE 'Asia/Bangkok')::date");
        }
    }
}
