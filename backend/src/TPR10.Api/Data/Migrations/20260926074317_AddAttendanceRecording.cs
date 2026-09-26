using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_sessions_id_user_id",
                table: "sessions",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateTable(
                name: "attendance_days",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_date = table.Column<DateOnly>(type: "date", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_in_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_out_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_days", x => x.id);
                    table.UniqueConstraint("AK_attendance_days_id_employee_id", x => new { x.id, x.employee_id });
                    table.CheckConstraint("ck_attendance_day_date", "work_date=(snapshot_at_utc AT TIME ZONE 'Asia/Bangkok')::date");
                    table.CheckConstraint("ck_attendance_day_time", "isfinite(work_date) AND isfinite(snapshot_at_utc) AND isfinite(effective_in_utc) AND (effective_out_utc IS NULL OR (isfinite(effective_out_utc) AND effective_out_utc>effective_in_utc))");
                    table.CheckConstraint("ck_attendance_day_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_attendance_days_employee_memberships_membership_id_employee~",
                        columns: x => new { x.membership_id, x.employee_id, x.workspace_id, x.department_id },
                        principalTable: "employee_memberships",
                        principalColumns: new[] { "id", "user_id", "workspace_id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_captured_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    accuracy_meters = table.Column<double>(type: "double precision", nullable: false),
                    gps_sampled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_events", x => x.id);
                    table.CheckConstraint("ck_attendance_event_action", "action IN ('CheckIn','CheckOut')");
                    table.CheckConstraint("ck_attendance_event_gps", "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180 AND accuracy_meters>0 AND accuracy_meters<'Infinity'::float8");
                    table.CheckConstraint("ck_attendance_event_source", "source='live'");
                    table.CheckConstraint("ck_attendance_event_time", "isfinite(occurred_at_utc) AND isfinite(device_captured_at_utc) AND isfinite(gps_sampled_at_utc)");
                    table.ForeignKey(
                        name: "FK_attendance_events_attendance_days_day_id_actor_id",
                        columns: x => new { x.day_id, x.actor_id },
                        principalTable: "attendance_days",
                        principalColumns: new[] { "id", "employee_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_evidence_objects_evidence_id",
                        column: x => x.evidence_id,
                        principalTable: "evidence_objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_sites_workspace_id_project_id_site_id",
                        columns: x => new { x.workspace_id, x.project_id, x.site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_day_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_day_version = table.Column<long>(type: "bigint", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Processing"),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fencing_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    result_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_operations", x => x.id);
                    table.UniqueConstraint("AK_attendance_operations_id_challenge_id", x => new { x.id, x.challenge_id });
                    table.CheckConstraint("ck_attendance_operation_day", "target_day_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_attendance_operation_hash", "payload_sha256 ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_attendance_operation_result", "(state='Committed' AND result_event_id IS NOT NULL AND result_json IS NOT NULL AND jsonb_typeof(result_json)='object' AND octet_length(result_json::text)<=4096) OR (state<>'Committed' AND result_event_id IS NULL AND result_json IS NULL)");
                    table.CheckConstraint("ck_attendance_operation_state", "state IN ('Processing','Committed','Expired','Failed')");
                    table.CheckConstraint("ck_attendance_operation_time", "isfinite(occurred_at_utc) AND isfinite(lease_until_utc) AND lease_until_utc>occurred_at_utc");
                    table.CheckConstraint("ck_attendance_operation_version", "version>=1 AND fencing_version>=1 AND (expected_day_version IS NULL OR expected_day_version>=1)");
                    table.ForeignKey(
                        name: "FK_attendance_operations_attendance_events_result_event_id",
                        column: x => x.result_event_id,
                        principalTable: "attendance_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_operations_employee_memberships_membership_id_ac~",
                        columns: x => new { x.membership_id, x.actor_id, x.membership_workspace_id, x.department_id },
                        principalTable: "employee_memberships",
                        principalColumns: new[] { "id", "user_id", "workspace_id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "capture_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_operation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_challenges", x => x.id);
                    table.UniqueConstraint("AK_capture_challenges_id_actor_id_session_id_action_workspace_~", x => new { x.id, x.actor_id, x.session_id, x.action, x.workspace_id, x.project_id, x.site_id, x.idempotency_key });
                    table.CheckConstraint("ck_capture_action", "action IN ('CheckIn','CheckOut')");
                    table.CheckConstraint("ck_capture_key", "idempotency_key<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_capture_time", "isfinite(issued_at_utc) AND isfinite(expires_at_utc) AND expires_at_utc=issued_at_utc+interval '120 seconds'");
                    table.ForeignKey(
                        name: "FK_capture_challenges_attendance_operations_consumed_operation~",
                        columns: x => new { x.consumed_operation_id, x.id },
                        principalTable: "attendance_operations",
                        principalColumns: new[] { "id", "challenge_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_capture_challenges_sessions_session_id_actor_id",
                        columns: x => new { x.session_id, x.actor_id },
                        principalTable: "sessions",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_capture_challenges_sites_workspace_id_project_id_site_id",
                        columns: x => new { x.workspace_id, x.project_id, x.site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_days_employee_id",
                table: "attendance_days",
                column: "employee_id",
                unique: true,
                filter: "effective_out_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_days_employee_id_work_date",
                table: "attendance_days",
                columns: new[] { "employee_id", "work_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_days_membership_id_employee_id_workspace_id_depa~",
                table: "attendance_days",
                columns: new[] { "membership_id", "employee_id", "workspace_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_day_id_action",
                table: "attendance_events",
                columns: new[] { "day_id", "action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_day_id_actor_id",
                table: "attendance_events",
                columns: new[] { "day_id", "actor_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_evidence_id",
                table: "attendance_events",
                column: "evidence_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_workspace_id_project_id_site_id",
                table: "attendance_events",
                columns: new[] { "workspace_id", "project_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_operations_actor_id_action_idempotency_key",
                table: "attendance_operations",
                columns: new[] { "actor_id", "action", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_operations_challenge_id",
                table: "attendance_operations",
                column: "challenge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_operations_challenge_id_actor_id_session_id_acti~",
                table: "attendance_operations",
                columns: new[] { "challenge_id", "actor_id", "session_id", "action", "workspace_id", "project_id", "site_id", "idempotency_key" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_operations_membership_id_actor_id_membership_wor~",
                table: "attendance_operations",
                columns: new[] { "membership_id", "actor_id", "membership_workspace_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_operations_result_event_id",
                table: "attendance_operations",
                column: "result_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_capture_challenges_actor_id_action_idempotency_key",
                table: "capture_challenges",
                columns: new[] { "actor_id", "action", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_capture_challenges_consumed_operation_id_id",
                table: "capture_challenges",
                columns: new[] { "consumed_operation_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_challenges_session_id_actor_id",
                table: "capture_challenges",
                columns: new[] { "session_id", "actor_id" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_challenges_workspace_id_project_id_site_id",
                table: "capture_challenges",
                columns: new[] { "workspace_id", "project_id", "site_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_attendance_operations_capture_challenges_challenge_id_actor~",
                table: "attendance_operations",
                columns: new[] { "challenge_id", "actor_id", "session_id", "action", "workspace_id", "project_id", "site_id", "idempotency_key" },
                principalTable: "capture_challenges",
                principalColumns: new[] { "id", "actor_id", "session_id", "action", "workspace_id", "project_id", "site_id", "idempotency_key" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(RecordingGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RecordingDownPreflight);
            migrationBuilder.DropForeignKey(
                name: "FK_attendance_events_attendance_days_day_id_actor_id",
                table: "attendance_events");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_operations_attendance_events_result_event_id",
                table: "attendance_operations");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_operations_capture_challenges_challenge_id_actor~",
                table: "attendance_operations");

            migrationBuilder.DropTable(
                name: "attendance_days");

            migrationBuilder.DropTable(
                name: "attendance_events");

            migrationBuilder.DropTable(
                name: "capture_challenges");

            migrationBuilder.DropTable(
                name: "attendance_operations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_sessions_id_user_id",
                table: "sessions");
            migrationBuilder.Sql(DropRecordingGuards);
        }
    }
}
