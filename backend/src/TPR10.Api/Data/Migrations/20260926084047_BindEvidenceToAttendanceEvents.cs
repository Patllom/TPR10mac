using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindEvidenceToAttendanceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BindingUpPreflight);
            migrationBuilder.AddForeignKey(
                name: "fk_evidence_binding_attendance_event",
                table: "evidence_bindings",
                column: "event_id",
                principalTable: "attendance_events",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(BindingGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BindingDownPreflight);
            migrationBuilder.Sql("DROP TRIGGER evidence_attendance_consistency ON evidence_bindings; DROP FUNCTION evidence_validate_attendance_binding();");
            migrationBuilder.DropForeignKey(
                name: "fk_evidence_binding_attendance_event",
                table: "evidence_bindings");
        }
    }
}
