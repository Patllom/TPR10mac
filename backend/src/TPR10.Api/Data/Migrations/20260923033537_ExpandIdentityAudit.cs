using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExpandIdentityAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing installations only: expand the catalog, never restore revoked grants.
            migrationBuilder.Sql("""
                INSERT INTO permissions(id,capability)
                SELECT id::uuid,capability FROM (VALUES
                  ('20000000-0000-0000-0000-000000000001','users:manage'),
                  ('20000000-0000-0000-0000-000000000002','audit:read'),
                  ('20000000-0000-0000-0000-000000000003','system:probe'),
                  ('20000000-0000-0000-0000-000000000004','roles:manage'),
                  ('20000000-0000-0000-0000-000000000005','roles:read'),
                  ('20000000-0000-0000-0000-000000000006','users:recover-mfa')
                ) AS catalog(id,capability)
                WHERE EXISTS (SELECT 1 FROM roles WHERE id='10000000-0000-0000-0000-000000000001')
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.AddColumn<Guid>(
                name: "acting_role_id",
                table: "audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "audit_events",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_type",
                table: "audit_events",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acting_role_id",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "target_type",
                table: "audit_events");
        }
    }
}
