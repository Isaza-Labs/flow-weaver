using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPythonModuleInstallTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InstallInSystem",
                table: "allowed_python_modules",
                type: "boolean",
                nullable: false,
                // Existing rows keep today's behaviour: installed into the
                // system interpreter's site dir.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "python_module_environments",
                columns: table => new
                {
                    AllowedPythonModuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PythonEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_python_module_environments", x => new { x.AllowedPythonModuleId, x.PythonEnvironmentId });
                    table.ForeignKey(
                        name: "FK_python_module_environments_allowed_python_modules_AllowedPy~",
                        column: x => x.AllowedPythonModuleId,
                        principalTable: "allowed_python_modules",
                        principalColumn: "AllowedPythonModuleId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_python_module_environments_python_environments_PythonEnviro~",
                        column: x => x.PythonEnvironmentId,
                        principalTable: "python_environments",
                        principalColumn: "PythonEnvironmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_python_module_environments_PythonEnvironmentId",
                table: "python_module_environments",
                column: "PythonEnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "python_module_environments");

            migrationBuilder.DropColumn(
                name: "InstallInSystem",
                table: "allowed_python_modules");
        }
    }
}
