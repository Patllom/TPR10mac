using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "technical_probes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technical_probes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_event_metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_event_metadata", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_event_metadata_audit_events_audit_event_id",
                        column: x => x.audit_event_id,
                        principalTable: "audit_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_metadata_audit_event_id_key",
                table: "audit_event_metadata",
                columns: new[] { "audit_event_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_correlation_id",
                table: "audit_events",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_occurred_at_utc",
                table: "audit_events",
                column: "occurred_at_utc");
            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_audit_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'audit records are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER audit_events_immutable BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION prevent_audit_mutation();
                CREATE TRIGGER audit_metadata_immutable BEFORE UPDATE OR DELETE ON audit_event_metadata
                    FOR EACH ROW EXECUTE FUNCTION prevent_audit_mutation();
                CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION prevent_audit_mutation();
                CREATE TRIGGER audit_metadata_no_truncate BEFORE TRUNCATE ON audit_event_metadata
                    FOR EACH STATEMENT EXECUTE FUNCTION prevent_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER audit_events_immutable ON audit_events;
                DROP TRIGGER audit_metadata_immutable ON audit_event_metadata;
                DROP TRIGGER audit_events_no_truncate ON audit_events;
                DROP TRIGGER audit_metadata_no_truncate ON audit_event_metadata;
                DROP FUNCTION prevent_audit_mutation();
                """);
            migrationBuilder.DropTable(
                name: "audit_event_metadata");

            migrationBuilder.DropTable(
                name: "technical_probes");

            migrationBuilder.DropTable(
                name: "audit_events");
        }
    }
}
