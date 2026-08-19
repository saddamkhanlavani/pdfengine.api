using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenantPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_TenantId",
                table: "UsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_PdfJobs_TenantId",
                table: "PdfJobs");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecord_Tenant_Environment_Timestamp",
                table: "UsageRecords",
                columns: new[] { "TenantId", "Environment", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_PdfJob_Tenant_Environment_Created",
                table: "PdfJobs",
                columns: new[] { "TenantId", "Environment", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_Tenant_Environment",
                table: "ApiKeys",
                columns: new[] { "TenantId", "Environment" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecord_Tenant_Environment_Timestamp",
                table: "UsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_PdfJob_Tenant_Environment_Created",
                table: "PdfJobs");

            migrationBuilder.DropIndex(
                name: "IX_ApiKey_Tenant_Environment",
                table: "ApiKeys");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_TenantId",
                table: "UsageRecords",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PdfJobs_TenantId",
                table: "PdfJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");
        }
    }
}
