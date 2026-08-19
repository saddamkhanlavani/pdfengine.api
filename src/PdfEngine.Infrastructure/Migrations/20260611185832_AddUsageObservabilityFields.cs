using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageObservabilityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthMechanism",
                table: "UsageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientIp",
                table: "UsageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsWatermarked",
                table: "UsageRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SandboxEnvironment",
                table: "UsageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "UsageRecords",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthMechanism",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ClientIp",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "IsWatermarked",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "SandboxEnvironment",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "UsageRecords");
        }
    }
}
