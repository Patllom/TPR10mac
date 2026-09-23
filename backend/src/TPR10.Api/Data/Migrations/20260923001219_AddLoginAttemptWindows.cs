using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginAttemptWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_attempt_windows",
                columns: table => new
                {
                    identifier_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    window_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_attempt_windows", x => x.identifier_hash);
                    table.CheckConstraint("ck_login_attempt_count", "failed_attempts >= 0 AND failed_attempts <= 5");
                    table.CheckConstraint("ck_login_identifier_hash", "octet_length(identifier_hash) = 32");
                });

            migrationBuilder.CreateIndex(
                name: "IX_login_attempt_windows_expires_at_utc",
                table: "login_attempt_windows",
                column: "expires_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_attempt_windows");
        }
    }
}
