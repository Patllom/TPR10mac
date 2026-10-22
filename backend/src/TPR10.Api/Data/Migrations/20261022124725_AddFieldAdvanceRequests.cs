using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvanceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_advance_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delegate_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    hold_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_requests", x => x.id);
                    table.UniqueConstraint("AK_field_advance_requests_workspace_id_project_id_id", x => new { x.workspace_id, x.project_id, x.id });
                    table.CheckConstraint("ck_advance_request_source", "source_request_id IS NULL OR source_request_id<>id");
                    table.CheckConstraint("ck_advance_request_version", "version>=1");
                    table.CheckConstraint("ck_field_advance_requests_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_requests_field_advance_requests_workspace_id_~",
                        columns: x => new { x.workspace_id, x.project_id, x.source_request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_requests_finance_persons_workspace_id_delegat~",
                        columns: x => new { x.workspace_id, x.delegate_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_requests_finance_persons_workspace_id_owner_p~",
                        columns: x => new { x.workspace_id, x.owner_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_requests_projects_workspace_id_project_id",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_requests_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_request_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_request_participants", x => x.id);
                    table.CheckConstraint("ck_advance_participant_role", "role IN ('Owner','Delegate','Approver','Checker','Payer')");
                    table.CheckConstraint("ck_field_advance_request_participants_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_request_participants_field_advance_requests_w~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_participants_finance_persons_workspac~",
                        columns: x => new { x.workspace_id, x.person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_participants_projects_workspace_id_pr~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_participants_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_request_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    company_policy_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_policy_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_snapshot_json = table.Column<string>(type: "jsonb", nullable: true),
                    policy_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    company_scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_request_revisions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_request_revisions_workspace_id_project_id_req~", x => new { x.workspace_id, x.project_id, x.request_id, x.id });
                    table.CheckConstraint("ck_advance_revision_company_scope", "company_scope_project_id='00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_advance_revision_content", "revision_number>0 AND total>=0 AND jsonb_typeof(content_json)='object' AND content_hash ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_advance_revision_policy", "(policy_snapshot_json IS NULL AND policy_hash IS NULL) OR (policy_snapshot_json IS NOT NULL AND policy_hash IS NOT NULL AND jsonb_typeof(policy_snapshot_json)='object' AND policy_hash ~ '^[a-f0-9]{64}$')");
                    table.CheckConstraint("ck_advance_revision_submission", "(submitted_at_utc IS NULL AND confirmed_by_person_id IS NULL) OR (submitted_at_utc IS NOT NULL AND isfinite(submitted_at_utc) AND confirmed_by_person_id IS NOT NULL AND company_policy_revision_id IS NOT NULL AND policy_snapshot_json IS NOT NULL AND total>0)");
                    table.CheckConstraint("ck_field_advance_request_revisions_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_field_advance_policy_revisi~",
                        columns: x => new { x.workspace_id, x.company_scope_project_id, x.company_policy_revision_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_field_advance_policy_revis~1",
                        columns: x => new { x.workspace_id, x.project_id, x.project_policy_revision_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_field_advance_requests_work~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id },
                        principalTable: "field_advance_requests",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_finance_persons_workspace_i~",
                        columns: x => new { x.workspace_id, x.confirmed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_finance_persons_workspace_~1",
                        columns: x => new { x.workspace_id, x.created_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_projects_workspace_id_proje~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_revisions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_approval_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    level_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    assignee_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    assignment_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    decided_by_person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_approval_steps", x => x.id);
                    table.UniqueConstraint("AK_field_advance_approval_steps_workspace_id_project_id_reques~", x => new { x.workspace_id, x.project_id, x.request_id, x.revision_id, x.id });
                    table.CheckConstraint("ck_advance_step_decision", "(outcome IS NULL AND decided_by_person_id IS NULL AND decided_by IS NULL AND decided_at_utc IS NULL AND decision_reason IS NULL) OR (outcome IS NOT NULL AND outcome IN ('Approved','Returned','Rejected') AND decided_by_person_id IS NOT NULL AND decided_by IS NOT NULL AND decided_at_utc IS NOT NULL AND isfinite(decided_at_utc) AND decision_reason IS NOT NULL AND length(btrim(decision_reason)) BETWEEN 1 AND 2000)");
                    table.CheckConstraint("ck_advance_step_order", "step_order>0 AND length(btrim(level_code))>0");
                    table.ForeignKey(
                        name: "FK_field_advance_approval_steps_field_advance_request_revision~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_request_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_approval_steps_finance_persons_workspace_id_a~",
                        columns: x => new { x.workspace_id, x.assignee_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_approval_steps_finance_persons_workspace_id_d~",
                        columns: x => new { x.workspace_id, x.decided_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_approval_steps_projects_workspace_id_project_~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_approval_steps_users_decided_by",
                        column: x => x.decided_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_request_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_order = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    unit_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_request_lines", x => x.id);
                    table.CheckConstraint("ck_advance_line_values", "line_order>0 AND amount>=0 AND ((mode='QuantityRate' AND quantity IS NOT NULL AND quantity>=0 AND unit_rate IS NOT NULL AND unit_rate>=0) OR (mode='LumpSum' AND quantity IS NULL AND unit_rate IS NULL))");
                    table.ForeignKey(
                        name: "FK_field_advance_request_lines_field_advance_request_revisions~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_request_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_lines_projects_workspace_id_project_id",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_request_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    data_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_request_events", x => x.id);
                    table.CheckConstraint("ck_advance_event_data", "aggregate_version>=1 AND jsonb_typeof(data_json)='object' AND length(btrim(kind))>0");
                    table.CheckConstraint("ck_field_advance_request_events_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_field_advance_approval_steps_w~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id, x.revision_id, x.step_id },
                        principalTable: "field_advance_approval_steps",
                        principalColumns: new[] { "workspace_id", "project_id", "request_id", "revision_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_field_advance_command_receipts~",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_field_advance_request_revision~",
                        columns: x => new { x.workspace_id, x.project_id, x.request_id, x.revision_id },
                        principalTable: "field_advance_request_revisions",
                        principalColumns: new[] { "workspace_id", "project_id", "request_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_finance_persons_workspace_id_a~",
                        columns: x => new { x.workspace_id, x.actor_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_projects_workspace_id_project_~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_request_events_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_approval_steps_decided_by",
                table: "field_advance_approval_steps",
                column: "decided_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_approval_steps_revision_id_decided_by_person_~",
                table: "field_advance_approval_steps",
                columns: new[] { "revision_id", "decided_by_person_id" },
                unique: true,
                filter: "outcome='Approved'");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_approval_steps_revision_id_step_order",
                table: "field_advance_approval_steps",
                columns: new[] { "revision_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_approval_steps_workspace_id_assignee_person_id",
                table: "field_advance_approval_steps",
                columns: new[] { "workspace_id", "assignee_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_approval_steps_workspace_id_decided_by_person~",
                table: "field_advance_approval_steps",
                columns: new[] { "workspace_id", "decided_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_events_actor_id",
                table: "field_advance_request_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_events_workspace_id_actor_person_id",
                table: "field_advance_request_events",
                columns: new[] { "workspace_id", "actor_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_events_workspace_id_command_id",
                table: "field_advance_request_events",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_events_workspace_id_project_id_reques~",
                table: "field_advance_request_events",
                columns: new[] { "workspace_id", "project_id", "request_id", "revision_id", "step_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_lines_revision_id_line_order",
                table: "field_advance_request_lines",
                columns: new[] { "revision_id", "line_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_lines_workspace_id_project_id_request~",
                table: "field_advance_request_lines",
                columns: new[] { "workspace_id", "project_id", "request_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_participants_created_by",
                table: "field_advance_request_participants",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_participants_request_id_person_id_role",
                table: "field_advance_request_participants",
                columns: new[] { "request_id", "person_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_participants_workspace_id_person_id",
                table: "field_advance_request_participants",
                columns: new[] { "workspace_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_participants_workspace_id_project_id_~",
                table: "field_advance_request_participants",
                columns: new[] { "workspace_id", "project_id", "request_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_created_by",
                table: "field_advance_request_revisions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_request_id",
                table: "field_advance_request_revisions",
                column: "request_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_request_id_revision_number",
                table: "field_advance_request_revisions",
                columns: new[] { "request_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_workspace_id_company_scope_~",
                table: "field_advance_request_revisions",
                columns: new[] { "workspace_id", "company_scope_project_id", "company_policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_workspace_id_confirmed_by_p~",
                table: "field_advance_request_revisions",
                columns: new[] { "workspace_id", "confirmed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_workspace_id_created_by_per~",
                table: "field_advance_request_revisions",
                columns: new[] { "workspace_id", "created_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_request_revisions_workspace_id_project_id_pro~",
                table: "field_advance_request_revisions",
                columns: new[] { "workspace_id", "project_id", "project_policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_created_by",
                table: "field_advance_requests",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_workspace_id_delegate_person_id",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "delegate_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_workspace_id_owner_person_id",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "owner_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_requests_workspace_id_project_id_source_reque~",
                table: "field_advance_requests",
                columns: new[] { "workspace_id", "project_id", "source_request_id" });
            migrationBuilder.Sql(RequestGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RemoveRequestGuards);
            migrationBuilder.DropTable(
                name: "field_advance_request_events");

            migrationBuilder.DropTable(
                name: "field_advance_request_lines");

            migrationBuilder.DropTable(
                name: "field_advance_request_participants");

            migrationBuilder.DropTable(
                name: "field_advance_approval_steps");

            migrationBuilder.DropTable(
                name: "field_advance_request_revisions");

            migrationBuilder.DropTable(
                name: "field_advance_requests");
        }
    }
}
