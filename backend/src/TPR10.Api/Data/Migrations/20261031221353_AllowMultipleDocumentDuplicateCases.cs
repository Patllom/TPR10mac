using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPR10.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleDocumentDuplicateCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_command_~",
                table: "field_advance_expense_duplicate_cases");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_document_id_other_doc~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "document_id", "other_document_id", "document_version", "other_document_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_command_~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "command_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_field_advance_expense_duplicate_cases_document_id_other_doc~",
                table: "field_advance_expense_duplicate_cases");

            migrationBuilder.DropIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_command_~",
                table: "field_advance_expense_duplicate_cases");

            migrationBuilder.CreateIndex(
                name: "IX_field_advance_expense_duplicate_cases_workspace_id_command_~",
                table: "field_advance_expense_duplicate_cases",
                columns: new[] { "workspace_id", "command_id" },
                unique: true);
        }
    }
}
