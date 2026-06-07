using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PdfEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductLayerSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Tenants");

            migrationBuilder.RenameColumn(
                name: "ErrorType",
                table: "UsageRecords",
                newName: "ErrorMessage");

            migrationBuilder.RenameColumn(
                name: "StripePriceId",
                table: "Tenants",
                newName: "TwoFactorSecret");

            migrationBuilder.RenameColumn(
                name: "ApiKey",
                table: "Tenants",
                newName: "PasswordHash");

            migrationBuilder.AlterColumn<string>(
                name: "RequestId",
                table: "UsageRecords",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "UsageRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoTopUpEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTwoFactorEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyHardLimit",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOn100Percent",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOn80Percent",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnNewInvoice",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId1",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_ApiKeyId",
                table: "UsageRecords",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId1",
                table: "ApiKeys",
                column: "TenantId1");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_Tenants_TenantId1",
                table: "ApiKeys",
                column: "TenantId1",
                principalTable: "Tenants",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_UsageRecords_ApiKeys_ApiKeyId",
                table: "UsageRecords",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_Tenants_TenantId1",
                table: "ApiKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_UsageRecords_ApiKeys_ApiKeyId",
                table: "UsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_ApiKeyId",
                table: "UsageRecords");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_TenantId1",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "AutoTopUpEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsTwoFactorEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MonthlyHardLimit",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "NotifyOn100Percent",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "NotifyOn80Percent",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "NotifyOnNewInvoice",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "TenantId1",
                table: "ApiKeys");

            migrationBuilder.RenameColumn(
                name: "ErrorMessage",
                table: "UsageRecords",
                newName: "ErrorType");

            migrationBuilder.RenameColumn(
                name: "TwoFactorSecret",
                table: "Tenants",
                newName: "StripePriceId");

            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                table: "Tenants",
                newName: "ApiKey");

            migrationBuilder.AlterColumn<string>(
                name: "RequestId",
                table: "UsageRecords",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
