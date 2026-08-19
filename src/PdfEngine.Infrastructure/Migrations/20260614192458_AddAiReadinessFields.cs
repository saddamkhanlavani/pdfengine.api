using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiReadinessFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EstimatedPageCount",
                table: "SavedTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Industry",
                table: "SavedTemplates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateType",
                table: "SavedTemplates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstimatedPageCount",
                table: "SavedTemplates");

            migrationBuilder.DropColumn(
                name: "Industry",
                table: "SavedTemplates");

            migrationBuilder.DropColumn(
                name: "TemplateType",
                table: "SavedTemplates");
        }
    }
}
