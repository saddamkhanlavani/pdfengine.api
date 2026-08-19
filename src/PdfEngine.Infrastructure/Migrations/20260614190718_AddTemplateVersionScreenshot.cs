using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateVersionScreenshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferenceScreenshotBase64",
                table: "SavedTemplateVersions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceScreenshotBase64",
                table: "SavedTemplateVersions");
        }
    }
}
