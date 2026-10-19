using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldAdvancePolicyFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "field_advance_command_receipts",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_command_receipts", x => x.command_id);
                    table.UniqueConstraint("AK_field_advance_command_receipts_workspace_id_command_id", x => new { x.workspace_id, x.command_id });
                    table.CheckConstraint("ck_field_advance_command_receipts_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_field_advance_receipt", "command_id<>'00000000-0000-0000-0000-000000000000'::uuid AND length(btrim(action))>0 AND payload_sha256 ~ '^[a-f0-9]{64}$' AND status BETWEEN 200 AND 299 AND jsonb_typeof(result_json)='object'");
                    table.ForeignKey(
                        name: "FK_field_advance_command_receipts_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_command_receipts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "finance_persons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_persons", x => x.id);
                    table.UniqueConstraint("AK_finance_persons_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.CheckConstraint("ck_finance_persons_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_finance_persons_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_finance_persons_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_finance_persons_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finance_persons_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_grant_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    delegable_capabilities = table.Column<string[]>(type: "text[]", nullable: false),
                    approval_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    starts_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(project_id,'00000000-0000-0000-0000-000000000000'::uuid)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_grant_proposals", x => x.id);
                    table.UniqueConstraint("AK_field_advance_grant_proposals_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.UniqueConstraint("AK_field_advance_grant_proposals_workspace_id_scope_project_id~", x => new { x.workspace_id, x.scope_project_id, x.id });
                    table.CheckConstraint("ck_field_advance_grant_amount", "approval_limit IS NULL OR approval_limit>=0");
                    table.CheckConstraint("ck_field_advance_grant_period", "isfinite(starts_at_utc) AND (ends_at_utc IS NULL OR (isfinite(ends_at_utc) AND ends_at_utc>starts_at_utc))");
                    table.CheckConstraint("ck_field_advance_grant_proposals_project", "project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_field_advance_grant_proposals_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_grant_proposals_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_field_advance_grant_proposals_version", "version>=1");
                    table.CheckConstraint("ck_field_advance_grant_state", "state IN ('Draft','Pending','Approved','Rejected','Withdrawn')");
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_finance_persons_workspace_id_~",
                        columns: x => new { x.workspace_id, x.proposed_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_finance_persons_workspace_id~1",
                        columns: x => new { x.workspace_id, x.recipient_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_projects_workspace_id_project~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_users_proposed_by",
                        column: x => x.proposed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_proposals_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_policy_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_company_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    clearing_days = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    company_scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000")),
                    scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(project_id,'00000000-0000-0000-0000-000000000000'::uuid)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_policy_revisions", x => x.id);
                    table.UniqueConstraint("AK_field_advance_policy_revisions_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.UniqueConstraint("AK_field_advance_policy_revisions_workspace_id_scope_project_i~", x => new { x.workspace_id, x.scope_project_id, x.id });
                    table.CheckConstraint("ck_field_advance_policy_base", "company_scope_project_id='00000000-0000-0000-0000-000000000000'::uuid AND ((project_id IS NULL AND base_company_version_id IS NULL) OR (project_id IS NOT NULL AND base_company_version_id IS NOT NULL))");
                    table.CheckConstraint("ck_field_advance_policy_content", "clearing_days>=0 AND content_hash ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("ck_field_advance_policy_revisions_project", "project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_field_advance_policy_revisions_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_policy_revisions_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_field_advance_policy_revisions_version", "version>=1");
                    table.CheckConstraint("ck_field_advance_policy_state", "state IN ('Draft','Pending','Approved','Active','Rejected','Retired')");
                    table.ForeignKey(
                        name: "FK_field_advance_policy_revisions_field_advance_policy_revisio~",
                        columns: x => new { x.workspace_id, x.company_scope_project_id, x.base_company_version_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_revisions_finance_persons_workspace_id~",
                        columns: x => new { x.workspace_id, x.created_by_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_revisions_projects_workspace_id_projec~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_revisions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_revisions_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "finance_account_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    end_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_account_links", x => x.id);
                    table.CheckConstraint("ck_finance_account_link_end", "(ended_at_utc IS NULL AND ended_by IS NULL AND end_reason IS NULL) OR\n(ended_at_utc IS NOT NULL AND ended_by IS NOT NULL AND end_reason IS NOT NULL\n AND isfinite(ended_at_utc) AND ended_at_utc>created_at_utc AND length(btrim(end_reason)) BETWEEN 1 AND 2000)");
                    table.CheckConstraint("ck_finance_account_links_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_finance_account_links_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_finance_account_links_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_finance_account_links_finance_persons_workspace_id_person_id",
                        columns: x => new { x.workspace_id, x.person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finance_account_links_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finance_account_links_users_ended_by",
                        column: x => x.ended_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finance_account_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finance_account_links_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_grant_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_grant_reviews", x => x.id);
                    table.UniqueConstraint("AK_field_advance_grant_reviews_workspace_id_proposal_id_id", x => new { x.workspace_id, x.proposal_id, x.id });
                    table.CheckConstraint("ck_field_advance_grant_reviews_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_grant_reviews_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_grant_reviews_field_advance_grant_proposals_w~",
                        columns: x => new { x.workspace_id, x.proposal_id },
                        principalTable: "field_advance_grant_proposals",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_reviews_finance_persons_workspace_id_re~",
                        columns: x => new { x.workspace_id, x.reviewer_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_reviews_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_reviews_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_active_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(project_id,'00000000-0000-0000-0000-000000000000'::uuid)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_active_policies", x => x.id);
                    table.CheckConstraint("ck_field_advance_active_policies_project", "project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_field_advance_active_policies_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_field_advance_active_policies_version", "version>=1");
                    table.ForeignKey(
                        name: "FK_field_advance_active_policies_field_advance_policy_revision~",
                        columns: x => new { x.workspace_id, x.scope_project_id, x.policy_revision_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_active_policies_projects_workspace_id_project~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_active_policies_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_active_policies_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_approval_bands",
                columns: table => new
                {
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    level_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    upper_inclusive = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_approval_bands", x => new { x.policy_revision_id, x.order });
                    table.CheckConstraint("ck_field_advance_band", "\"order\">0 AND length(btrim(level_code))>0 AND upper_inclusive>=0");
                    table.ForeignKey(
                        name: "FK_field_advance_approval_bands_field_advance_policy_revisions~",
                        column: x => x.policy_revision_id,
                        principalTable: "field_advance_policy_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_category_rules",
                columns: table => new
                {
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    standard_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    evidence_types = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_category_rules", x => new { x.policy_revision_id, x.id });
                    table.CheckConstraint("ck_field_advance_category", "length(btrim(name))>0 AND (standard_rate IS NULL OR standard_rate>=0)");
                    table.ForeignKey(
                        name: "FK_field_advance_category_rules_field_advance_policy_revisions~",
                        column: x => x.policy_revision_id,
                        principalTable: "field_advance_policy_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_policy_contributors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_policy_contributors", x => x.id);
                    table.CheckConstraint("ck_field_advance_policy_contributors_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_policy_contributors_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_policy_contributors_field_advance_policy_revi~",
                        columns: x => new { x.workspace_id, x.policy_revision_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_contributors_finance_persons_workspace~",
                        columns: x => new { x.workspace_id, x.person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_contributors_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_contributors_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_policy_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_policy_reviews", x => x.id);
                    table.CheckConstraint("ck_field_advance_policy_reviews_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_policy_reviews_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_policy_reviews_field_advance_policy_revisions~",
                        columns: x => new { x.workspace_id, x.policy_revision_id },
                        principalTable: "field_advance_policy_revisions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_reviews_finance_persons_workspace_id_r~",
                        columns: x => new { x.workspace_id, x.reviewer_person_id },
                        principalTable: "finance_persons",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_reviews_users_reviewer_id",
                        column: x => x.reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_policy_reviews_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_grant_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revocation_reason = table.Column<string>(type: "text", nullable: true),
                    scope_project_id = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(project_id,'00000000-0000-0000-0000-000000000000'::uuid)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_grant_assignments", x => x.id);
                    table.UniqueConstraint("AK_field_advance_grant_assignments_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.CheckConstraint("ck_field_advance_grant_assignments_project", "project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_field_advance_grant_assignments_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_grant_assignments_time", "isfinite(created_at_utc)");
                    table.CheckConstraint("ck_field_advance_grant_assignments_version", "version>=1");
                    table.CheckConstraint("ck_field_advance_grant_revocation", "(revoked_at_utc IS NULL AND revoked_by IS NULL AND revocation_reason IS NULL) OR\n(revoked_at_utc IS NOT NULL AND revoked_by IS NOT NULL AND revocation_reason IS NOT NULL\n AND isfinite(revoked_at_utc) AND revoked_at_utc>=created_at_utc AND length(btrim(revocation_reason)) BETWEEN 1 AND 2000)");
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_field_advance_grant_proposa~",
                        columns: x => new { x.workspace_id, x.scope_project_id, x.proposal_id },
                        principalTable: "field_advance_grant_proposals",
                        principalColumns: new[] { "workspace_id", "scope_project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_field_advance_grant_reviews~",
                        columns: x => new { x.workspace_id, x.proposal_id, x.review_id },
                        principalTable: "field_advance_grant_reviews",
                        principalColumns: new[] { "workspace_id", "proposal_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_projects_workspace_id_proje~",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_users_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_grant_assignments_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_advance_bootstrap_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_advance_bootstrap_receipts", x => x.id);
                    table.CheckConstraint("ck_field_advance_bootstrap_receipts_reason", "length(btrim(reason)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_field_advance_bootstrap_receipts_time", "isfinite(created_at_utc)");
                    table.ForeignKey(
                        name: "FK_field_advance_bootstrap_receipts_field_advance_command_rece~",
                        columns: x => new { x.workspace_id, x.command_id },
                        principalTable: "field_advance_command_receipts",
                        principalColumns: new[] { "workspace_id", "command_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_bootstrap_receipts_field_advance_grant_assign~",
                        columns: x => new { x.workspace_id, x.grant_assignment_id },
                        principalTable: "field_advance_grant_assignments",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_bootstrap_receipts_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_advance_bootstrap_receipts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_active_policies_created_by",
                table: "field_advance_active_policies",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_active_policies_workspace_id_project_id",
                table: "field_advance_active_policies",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_active_policies_workspace_id_scope_project_id",
                table: "field_advance_active_policies",
                columns: new[] { "workspace_id", "scope_project_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_active_policies_workspace_id_scope_project_id~",
                table: "field_advance_active_policies",
                columns: new[] { "workspace_id", "scope_project_id", "policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_bootstrap_receipts_command_id_grant_assignmen~",
                table: "field_advance_bootstrap_receipts",
                columns: new[] { "command_id", "grant_assignment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_bootstrap_receipts_created_by",
                table: "field_advance_bootstrap_receipts",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_bootstrap_receipts_workspace_id_command_id",
                table: "field_advance_bootstrap_receipts",
                columns: new[] { "workspace_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_bootstrap_receipts_workspace_id_grant_assignm~",
                table: "field_advance_bootstrap_receipts",
                columns: new[] { "workspace_id", "grant_assignment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_command_receipts_actor_id",
                table: "field_advance_command_receipts",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_created_by",
                table: "field_advance_grant_assignments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_proposal_id",
                table: "field_advance_grant_assignments",
                column: "proposal_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_revoked_by",
                table: "field_advance_grant_assignments",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_workspace_id_project_id",
                table: "field_advance_grant_assignments",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_workspace_id_proposal_id_re~",
                table: "field_advance_grant_assignments",
                columns: new[] { "workspace_id", "proposal_id", "review_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_assignments_workspace_id_scope_project_~",
                table: "field_advance_grant_assignments",
                columns: new[] { "workspace_id", "scope_project_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_proposals_proposed_by",
                table: "field_advance_grant_proposals",
                column: "proposed_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_proposals_recipient_id",
                table: "field_advance_grant_proposals",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_proposals_workspace_id_project_id",
                table: "field_advance_grant_proposals",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_proposals_workspace_id_proposed_by_pers~",
                table: "field_advance_grant_proposals",
                columns: new[] { "workspace_id", "proposed_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_proposals_workspace_id_recipient_person~",
                table: "field_advance_grant_proposals",
                columns: new[] { "workspace_id", "recipient_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_reviews_reviewer_id",
                table: "field_advance_grant_reviews",
                column: "reviewer_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_grant_reviews_workspace_id_reviewer_person_id",
                table: "field_advance_grant_reviews",
                columns: new[] { "workspace_id", "reviewer_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_contributors_policy_revision_id_person~",
                table: "field_advance_policy_contributors",
                columns: new[] { "policy_revision_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_contributors_user_id",
                table: "field_advance_policy_contributors",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_contributors_workspace_id_person_id",
                table: "field_advance_policy_contributors",
                columns: new[] { "workspace_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_contributors_workspace_id_policy_revis~",
                table: "field_advance_policy_contributors",
                columns: new[] { "workspace_id", "policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_reviews_reviewer_id",
                table: "field_advance_policy_reviews",
                column: "reviewer_id");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_reviews_workspace_id_policy_revision_id",
                table: "field_advance_policy_reviews",
                columns: new[] { "workspace_id", "policy_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_reviews_workspace_id_reviewer_person_id",
                table: "field_advance_policy_reviews",
                columns: new[] { "workspace_id", "reviewer_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_created_by",
                table: "field_advance_policy_revisions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_company_scope_p~",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "company_scope_project_id", "base_company_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_created_by_pers~",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "created_by_person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_policy_revisions_workspace_id_project_id",
                table: "field_advance_policy_revisions",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_finance_account_links_created_by",
                table: "finance_account_links",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_finance_account_links_ended_by",
                table: "finance_account_links",
                column: "ended_by");

            migrationBuilder.CreateIndex(
                name: "IX_finance_account_links_user_id",
                table: "finance_account_links",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_finance_account_links_workspace_id_person_id",
                table: "finance_account_links",
                columns: new[] { "workspace_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "IX_finance_account_links_workspace_id_user_id",
                table: "finance_account_links",
                columns: new[] { "workspace_id", "user_id" },
                unique: true,
                filter: "ended_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_finance_persons_created_by",
                table: "finance_persons",
                column: "created_by");
            migrationBuilder.Sql(HistoryGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AssertEmptyFoundation);
            migrationBuilder.Sql(DropHistoryGuards);
            migrationBuilder.DropTable(
                name: "field_advance_active_policies");

            migrationBuilder.DropTable(
                name: "field_advance_approval_bands");

            migrationBuilder.DropTable(
                name: "field_advance_bootstrap_receipts");

            migrationBuilder.DropTable(
                name: "field_advance_category_rules");

            migrationBuilder.DropTable(
                name: "field_advance_policy_contributors");

            migrationBuilder.DropTable(
                name: "field_advance_policy_reviews");

            migrationBuilder.DropTable(
                name: "finance_account_links");

            migrationBuilder.DropTable(
                name: "field_advance_command_receipts");

            migrationBuilder.DropTable(
                name: "field_advance_grant_assignments");

            migrationBuilder.DropTable(
                name: "field_advance_policy_revisions");

            migrationBuilder.DropTable(
                name: "field_advance_grant_reviews");

            migrationBuilder.DropTable(
                name: "field_advance_grant_proposals");

            migrationBuilder.DropTable(
                name: "finance_persons");
        }
    }
}
