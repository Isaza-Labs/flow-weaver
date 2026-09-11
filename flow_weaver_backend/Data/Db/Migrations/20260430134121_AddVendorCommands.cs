using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vendor_commands",
                columns: table => new
                {
                    VendorCommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceType = table.Column<string>(type: "text", nullable: false),
                    VendorFamily = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_commands", x => x.VendorCommandId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_CompanyId",
                table: "vendor_commands",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_CompanyId_DeviceType_Kind",
                table: "vendor_commands",
                columns: new[] { "CompanyId", "DeviceType", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_CompanyId_DeviceType_Kind_Value",
                table: "vendor_commands",
                columns: new[] { "CompanyId", "DeviceType", "Kind", "Value" },
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vendor_commands");
        }
    }
}
