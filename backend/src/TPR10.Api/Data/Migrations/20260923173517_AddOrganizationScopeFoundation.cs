using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationScopeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "domain",
                table: "permissions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.Sql("""
                INSERT INTO permissions(id,capability,domain) VALUES
                  ('20000000-0000-0000-0000-000000000007','organization:manage','system'),
                  ('20000000-0000-0000-0000-000000000008','scope-assignments:manage','system'),
                  ('20000000-0000-0000-0000-000000000009','scope-probe:read','scoped-business'),
                  ('20000000-0000-0000-0000-000000000010','scope-probe:write','scoped-business'),
                  ('20000000-0000-0000-0000-000000000011','scope-probe:export','scoped-business'),
                  ('20000000-0000-0000-0000-000000000012','scope-probe:restricted-read','scoped-business');
                INSERT INTO role_permissions(role_id,permission_id)
                SELECT r.id,p.id FROM roles r CROSS JOIN permissions p
                WHERE r.id='10000000-0000-0000-0000-000000000001'
                  AND p.id IN ('20000000-0000-0000-0000-000000000007','20000000-0000-0000-0000-000000000008');
                """);

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.id);
                    table.CheckConstraint("ck_workspaces_code", "code ~ '^[A-Z0-9_-]{1,64}$'");
                    table.CheckConstraint("ck_workspaces_name", "length(name) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_workspaces_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_workspaces_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspaces_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.id);
                    table.CheckConstraint("ck_departments_code", "code ~ '^[A-Z0-9_-]{1,64}$'");
                    table.CheckConstraint("ck_departments_name", "length(name) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_departments_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_departments_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_departments_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_departments_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.UniqueConstraint("AK_projects_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.CheckConstraint("ck_projects_code", "code ~ '^[A-Z0-9_-]{1,64}$'");
                    table.CheckConstraint("ck_projects_name", "length(name) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_projects_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_projects_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_projects_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_projects_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.id);
                    table.UniqueConstraint("AK_sites_workspace_id_project_id_id", x => new { x.workspace_id, x.project_id, x.id });
                    table.CheckConstraint("ck_sites_code", "code ~ '^[A-Z0-9_-]{1,64}$'");
                    table.CheckConstraint("ck_sites_name", "length(name) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_sites_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_sites_projects_workspace_id_project_id",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sites_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sites_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scope_probe_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    restricted_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scope_probe_records", x => x.id);
                    table.CheckConstraint("ck_scope_probe_records_shape", "site_id IS NULL OR project_id IS NOT NULL");
                    table.CheckConstraint("ck_scope_probe_records_version", "version >= 1");
                    table.CheckConstraint("ck_scope_record_note", "note !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_scope_record_restricted_note", "restricted_note IS NULL OR restricted_note !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_scope_probe_records_projects_workspace_id_project_id",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scope_probe_records_sites_workspace_id_project_id_site_id",
                        columns: x => new { x.workspace_id, x.project_id, x.site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scope_probe_records_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scope_probe_records_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scope_probe_records_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_scope_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_scope_assignments", x => x.id);
                    table.CheckConstraint("ck_assignment_reason", "length(reason) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_assignment_revocation", "(revoked_at_utc IS NULL AND revoked_by IS NULL AND revocation_reason IS NULL)\nOR (revoked_at_utc IS NOT NULL AND revoked_by IS NOT NULL AND revocation_reason IS NOT NULL\n    AND length(revocation_reason) BETWEEN 1 AND 500 AND revocation_reason !~ '[[:cntrl:]]')");
                    table.CheckConstraint("ck_user_scope_assignments_shape", "site_id IS NULL OR project_id IS NOT NULL");
                    table.CheckConstraint("ck_user_scope_assignments_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_projects_workspace_id_project_id",
                        columns: x => new { x.workspace_id, x.project_id },
                        principalTable: "projects",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_sites_workspace_id_project_id_site_id",
                        columns: x => new { x.workspace_id, x.project_id, x.site_id },
                        principalTable: "sites",
                        principalColumns: new[] { "workspace_id", "project_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_users_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_scope_assignments_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions",
                sql: "domain IN ('system','scoped-business')");

            migrationBuilder.CreateIndex(
                name: "IX_departments_created_by",
                table: "departments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_departments_updated_by",
                table: "departments",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "IX_departments_workspace_id_code",
                table: "departments",
                columns: new[] { "workspace_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_created_by",
                table: "projects",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_projects_updated_by",
                table: "projects",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "IX_projects_workspace_id_code",
                table: "projects",
                columns: new[] { "workspace_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scope_probe_records_created_by",
                table: "scope_probe_records",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_scope_probe_records_updated_by",
                table: "scope_probe_records",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "IX_scope_probe_records_workspace_id_project_id_site_id_created~",
                table: "scope_probe_records",
                columns: new[] { "workspace_id", "project_id", "site_id", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_sites_created_by",
                table: "sites",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_sites_project_id_code",
                table: "sites",
                columns: new[] { "project_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sites_updated_by",
                table: "sites",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "IX_user_scope_assignments_created_by",
                table: "user_scope_assignments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_user_scope_assignments_revoked_by",
                table: "user_scope_assignments",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "IX_user_scope_assignments_role_id",
                table: "user_scope_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_scope_assignments_workspace_id_project_id_site_id",
                table: "user_scope_assignments",
                columns: new[] { "workspace_id", "project_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ux_assignment_project",
                table: "user_scope_assignments",
                columns: new[] { "user_id", "workspace_id", "project_id", "role_id" },
                unique: true,
                filter: "revoked_at_utc IS NULL AND project_id IS NOT NULL AND site_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_assignment_site",
                table: "user_scope_assignments",
                columns: new[] { "user_id", "workspace_id", "project_id", "site_id", "role_id" },
                unique: true,
                filter: "revoked_at_utc IS NULL AND site_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_assignment_workspace",
                table: "user_scope_assignments",
                columns: new[] { "user_id", "workspace_id", "role_id" },
                unique: true,
                filter: "revoked_at_utc IS NULL AND project_id IS NULL AND site_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_code",
                table: "workspaces",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_created_by",
                table: "workspaces",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_updated_by",
                table: "workspaces",
                column: "updated_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A downgrade discards this module's grants; use only on disposable databases
            // or after an explicit data-preservation plan for real scope data.
            migrationBuilder.Sql("""
                DELETE FROM role_permissions WHERE permission_id IN (
                  '20000000-0000-0000-0000-000000000007','20000000-0000-0000-0000-000000000008',
                  '20000000-0000-0000-0000-000000000009','20000000-0000-0000-0000-000000000010',
                  '20000000-0000-0000-0000-000000000011','20000000-0000-0000-0000-000000000012');
                DELETE FROM permissions WHERE id IN (
                  '20000000-0000-0000-0000-000000000007','20000000-0000-0000-0000-000000000008',
                  '20000000-0000-0000-0000-000000000009','20000000-0000-0000-0000-000000000010',
                  '20000000-0000-0000-0000-000000000011','20000000-0000-0000-0000-000000000012');
                """);
            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "scope_probe_records");

            migrationBuilder.DropTable(
                name: "user_scope_assignments");

            migrationBuilder.DropTable(
                name: "sites");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "domain",
                table: "permissions");
        }
    }
}
