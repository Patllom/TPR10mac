using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_departments_id_workspace_id",
                table: "departments",
                columns: new[] { "id", "workspace_id" });

            migrationBuilder.CreateTable(
                name: "employee_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_memberships", x => x.id);
                    table.UniqueConstraint("AK_employee_memberships_id_user_id", x => new { x.id, x.user_id });
                    table.CheckConstraint("ck_employee_memberships_period", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
                    table.CheckConstraint("ck_employee_memberships_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_employee_memberships_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_employee_memberships_departments_department_id_workspace_id",
                        columns: x => new { x.department_id, x.workspace_id },
                        principalTable: "departments",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_memberships_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_memberships_users_ended_by",
                        column: x => x.ended_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_employee_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "hr_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hr_assignments", x => x.id);
                    table.CheckConstraint("ck_hr_assignments_period", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
                    table.CheckConstraint("ck_hr_assignments_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_hr_assignments_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_hr_assignments_departments_department_id_workspace_id",
                        columns: x => new { x.department_id, x.workspace_id },
                        principalTable: "departments",
                        principalColumns: new[] { "id", "workspace_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_assignments_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_assignments_users_ended_by",
                        column: x => x.ended_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_hr_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reporting_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supervisor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reporting_lines", x => x.id);
                    table.CheckConstraint("ck_reporting_lines_not_self", "employee_user_id <> supervisor_user_id");
                    table.CheckConstraint("ck_reporting_lines_period", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
                    table.CheckConstraint("ck_reporting_lines_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
                    table.CheckConstraint("ck_reporting_lines_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_reporting_lines_employee_memberships_employee_membership_id~",
                        columns: x => new { x.employee_membership_id, x.employee_user_id },
                        principalTable: "employee_memberships",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reporting_lines_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reporting_lines_users_ended_by",
                        column: x => x.ended_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reporting_lines_users_supervisor_user_id",
                        column: x => x.supervisor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions",
                sql: "domain IN ('system','scoped-business','attendance')");

            migrationBuilder.CreateIndex(
                name: "IX_employee_memberships_created_by",
                table: "employee_memberships",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_employee_memberships_department_id_workspace_id",
                table: "employee_memberships",
                columns: new[] { "department_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "IX_employee_memberships_ended_by",
                table: "employee_memberships",
                column: "ended_by");

            migrationBuilder.CreateIndex(
                name: "IX_employee_memberships_user_id_valid_from_utc_valid_to_utc",
                table: "employee_memberships",
                columns: new[] { "user_id", "valid_from_utc", "valid_to_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_assignments_created_by",
                table: "hr_assignments",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_hr_assignments_department_id_workspace_id",
                table: "hr_assignments",
                columns: new[] { "department_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "IX_hr_assignments_ended_by",
                table: "hr_assignments",
                column: "ended_by");

            migrationBuilder.CreateIndex(
                name: "IX_hr_assignments_user_id_workspace_id_department_id_valid_fro~",
                table: "hr_assignments",
                columns: new[] { "user_id", "workspace_id", "department_id", "valid_from_utc", "valid_to_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_created_by",
                table: "reporting_lines",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_employee_membership_id_employee_user_id",
                table: "reporting_lines",
                columns: new[] { "employee_membership_id", "employee_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_employee_user_id_valid_from_utc_valid_to_utc",
                table: "reporting_lines",
                columns: new[] { "employee_user_id", "valid_from_utc", "valid_to_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_ended_by",
                table: "reporting_lines",
                column: "ended_by");

            migrationBuilder.CreateIndex(
                name: "IX_reporting_lines_supervisor_user_id",
                table: "reporting_lines",
                column: "supervisor_user_id");
            migrationBuilder.Sql("""
                INSERT INTO permissions(id,capability,domain) VALUES
                  ('20000000-0000-0000-0000-000000000013','attendance:record','scoped-business'),
                  ('20000000-0000-0000-0000-000000000014','attendance:team-read','attendance'),
                  ('20000000-0000-0000-0000-000000000015','attendance:hr-read','attendance'),
                  ('20000000-0000-0000-0000-000000000016','attendance:approve-supervisor','attendance'),
                  ('20000000-0000-0000-0000-000000000017','attendance:approve-hr','attendance'),
                  ('20000000-0000-0000-0000-000000000018','attendance:directory-manage','system'),
                  ('20000000-0000-0000-0000-000000000019','attendance:storage-manage','system');
                """);

            // VOLATILE uses a fresh READ COMMITTED snapshot after acquiring the shared identity lock.
            migrationBuilder.Sql("""
                CREATE FUNCTION attendance_employee_memberships_no_overlap() RETURNS trigger
                LANGUAGE plpgsql VOLATILE AS $$
                BEGIN
                    IF current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'Attendance directory writes require READ COMMITTED'
                            USING ERRCODE = '25001';
                    END IF;
                    PERFORM pg_advisory_xact_lock(7241002);
                    IF EXISTS (
                        SELECT 1 FROM employee_memberships existing
                        WHERE existing.user_id = NEW.user_id
                          AND existing.id <> NEW.id
                          AND NEW.valid_from_utc < COALESCE(existing.valid_to_utc, 'infinity'::timestamptz)
                          AND existing.valid_from_utc < COALESCE(NEW.valid_to_utc, 'infinity'::timestamptz)
                    ) THEN
                        RAISE EXCEPTION 'Overlapping attendance directory interval'
                            USING ERRCODE = '23P01', CONSTRAINT = 'ex_employee_memberships_period';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER attendance_no_overlap BEFORE INSERT OR UPDATE ON employee_memberships
                FOR EACH ROW EXECUTE FUNCTION attendance_employee_memberships_no_overlap();
                """);

            // VOLATILE uses a fresh READ COMMITTED snapshot after acquiring the shared identity lock.
            migrationBuilder.Sql("""
                CREATE FUNCTION attendance_reporting_lines_no_overlap() RETURNS trigger
                LANGUAGE plpgsql VOLATILE AS $$
                BEGIN
                    IF current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'Attendance directory writes require READ COMMITTED'
                            USING ERRCODE = '25001';
                    END IF;
                    PERFORM pg_advisory_xact_lock(7241002);
                    IF EXISTS (
                        SELECT 1 FROM reporting_lines existing
                        WHERE existing.employee_user_id = NEW.employee_user_id
                          AND existing.id <> NEW.id
                          AND NEW.valid_from_utc < COALESCE(existing.valid_to_utc, 'infinity'::timestamptz)
                          AND existing.valid_from_utc < COALESCE(NEW.valid_to_utc, 'infinity'::timestamptz)
                    ) THEN
                        RAISE EXCEPTION 'Overlapping attendance directory interval'
                            USING ERRCODE = '23P01', CONSTRAINT = 'ex_reporting_lines_period';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER attendance_no_overlap BEFORE INSERT OR UPDATE ON reporting_lines
                FOR EACH ROW EXECUTE FUNCTION attendance_reporting_lines_no_overlap();
                """);

            // VOLATILE uses a fresh READ COMMITTED snapshot after acquiring the shared identity lock.
            migrationBuilder.Sql("""
                CREATE FUNCTION attendance_hr_assignments_no_overlap() RETURNS trigger
                LANGUAGE plpgsql VOLATILE AS $$
                BEGIN
                    IF current_setting('transaction_isolation') <> 'read committed' THEN
                        RAISE EXCEPTION 'Attendance directory writes require READ COMMITTED'
                            USING ERRCODE = '25001';
                    END IF;
                    PERFORM pg_advisory_xact_lock(7241002);
                    IF EXISTS (
                        SELECT 1 FROM hr_assignments existing
                        WHERE existing.user_id = NEW.user_id AND existing.workspace_id = NEW.workspace_id AND existing.department_id = NEW.department_id
                          AND existing.id <> NEW.id
                          AND NEW.valid_from_utc < COALESCE(existing.valid_to_utc, 'infinity'::timestamptz)
                          AND existing.valid_from_utc < COALESCE(NEW.valid_to_utc, 'infinity'::timestamptz)
                    ) THEN
                        RAISE EXCEPTION 'Overlapping attendance directory interval'
                            USING ERRCODE = '23P01', CONSTRAINT = 'ex_hr_assignments_period';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER attendance_no_overlap BEFORE INSERT OR UPDATE ON hr_assignments
                FOR EACH ROW EXECUTE FUNCTION attendance_hr_assignments_no_overlap();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse destructive rollback; do not silently delete business history or grants.
            migrationBuilder.Sql("""
                LOCK TABLE employee_memberships, reporting_lines, hr_assignments, permissions, role_permissions
                    IN ACCESS EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM employee_memberships)
                        OR EXISTS (SELECT 1 FROM reporting_lines)
                        OR EXISTS (SELECT 1 FROM hr_assignments)
                        OR EXISTS (
                            SELECT 1 FROM role_permissions rp JOIN permissions p ON p.id = rp.permission_id
                            WHERE p.domain = 'attendance'
                               OR p.id BETWEEN '20000000-0000-0000-0000-000000000013'::uuid
                                           AND '20000000-0000-0000-0000-000000000019'::uuid
                        ) THEN
                        RAISE EXCEPTION 'Cannot downgrade attendance directory while history or grants exist';
                    END IF;
                END;
                $$;
                DELETE FROM permissions WHERE id BETWEEN '20000000-0000-0000-0000-000000000013'::uuid
                    AND '20000000-0000-0000-0000-000000000019'::uuid;
                """);
            migrationBuilder.DropTable(
                name: "hr_assignments");

            migrationBuilder.DropTable(
                name: "reporting_lines");

            migrationBuilder.DropTable(
                name: "employee_memberships");

            migrationBuilder.Sql("""
                DROP FUNCTION attendance_employee_memberships_no_overlap();
                DROP FUNCTION attendance_reporting_lines_no_overlap();
                DROP FUNCTION attendance_hr_assignments_no_overlap();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_departments_id_workspace_id",
                table: "departments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_permissions_domain",
                table: "permissions",
                sql: "domain IN ('system','scoped-business')");
        }
    }
}
