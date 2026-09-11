using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    PermissionGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    SubjectIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Capabilities = table.Column<List<string>>(type: "text[]", nullable: false),
                    Conditions = table.Column<string>(type: "jsonb", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_grants", x => x.PermissionGrantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_CompanyId",
                table: "permission_grants",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_CompanyId_Enabled",
                table: "permission_grants",
                columns: new[] { "CompanyId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_CompanyId_Name",
                table: "permission_grants",
                columns: new[] { "CompanyId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_grants");
        }
    }
}
