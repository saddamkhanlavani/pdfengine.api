using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileUrlToUsageRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileUrl",
                table: "UsageRecords",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileUrl",
                table: "UsageRecords");
        }
    }
}
