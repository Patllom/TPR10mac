using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProtectAttendanceDirectoryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION attendance_preserve_closed_history() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.valid_to_utc IS NOT NULL OR TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Attendance directory history is immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_attendance_history_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER attendance_history BEFORE UPDATE OR DELETE ON employee_memberships
                    FOR EACH ROW EXECUTE FUNCTION attendance_preserve_closed_history();
                CREATE TRIGGER attendance_history BEFORE UPDATE OR DELETE ON reporting_lines
                    FOR EACH ROW EXECUTE FUNCTION attendance_preserve_closed_history();
                CREATE TRIGGER attendance_history BEFORE UPDATE OR DELETE ON hr_assignments
                    FOR EACH ROW EXECUTE FUNCTION attendance_preserve_closed_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE employee_memberships, reporting_lines, hr_assignments, permissions, role_permissions
                    IN ACCESS EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM employee_memberships)
                        OR EXISTS (SELECT 1 FROM reporting_lines)
                        OR EXISTS (SELECT 1 FROM hr_assignments)
                        OR EXISTS (SELECT 1 FROM role_permissions rp JOIN permissions p ON p.id=rp.permission_id
                            WHERE p.domain='attendance' OR p.capability LIKE 'attendance:%') THEN
                        RAISE EXCEPTION 'Cannot downgrade attendance history protection while history or grants exist';
                    END IF;
                END;
                $$;
                DROP TRIGGER attendance_history ON employee_memberships;
                DROP TRIGGER attendance_history ON reporting_lines;
                DROP TRIGGER attendance_history ON hr_assignments;
                DROP FUNCTION attendance_preserve_closed_history();
                """);
        }
    }
}
