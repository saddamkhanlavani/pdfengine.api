using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosticsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiagnosticsJson",
                table: "PdfJobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiagnosticsJson",
                table: "PdfJobs");
        }
    }
}
