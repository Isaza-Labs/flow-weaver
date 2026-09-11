using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class MakeDeviceSourceExternalOptionalPerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceId",
                table: "devices",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalId",
                table: "devices",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql("""
                UPDATE devices
                SET "SourceId" = NULL
                WHERE "SourceId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.Sql("""
                UPDATE devices
                SET "ExternalId" = NULL
                WHERE "ExternalId" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "CompanyId", "SourceId", "ExternalId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId_SourceId_ExternalId",
                table: "devices");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceId",
                table: "devices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalId",
                table: "devices",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);
        }
    }
}
