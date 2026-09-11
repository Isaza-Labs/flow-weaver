using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AllowedPythonModule_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allowed_python_modules",
                columns: table => new
                {
                    AllowedPythonModuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportName = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    PipSpec = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    InstalledVersion = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allowed_python_modules", x => x.AllowedPythonModuleId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_CompanyId",
                table: "allowed_python_modules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_CompanyId_ImportName",
                table: "allowed_python_modules",
                columns: new[] { "CompanyId", "ImportName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allowed_python_modules");
        }
    }
}
