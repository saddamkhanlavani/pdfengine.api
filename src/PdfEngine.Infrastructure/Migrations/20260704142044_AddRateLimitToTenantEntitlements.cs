using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitToTenantEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "TenantEntitlements",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "TenantEntitlements");
        }
    }
}
