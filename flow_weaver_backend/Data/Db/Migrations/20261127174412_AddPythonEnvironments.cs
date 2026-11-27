using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddPythonEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PythonEnvironmentId",
                table: "snippets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PythonEnvironmentId",
                table: "devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "python_environments",
                columns: table => new
                {
                    PythonEnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PythonVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Requirements = table.Column<string>(type: "text", nullable: false),
                    IsSshFallback = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ResolvedPackages = table.Column<string>(type: "text", nullable: true),
                    RequirementsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_python_environments", x => x.PythonEnvironmentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_python_environments_IsSshFallback",
                table: "python_environments",
                column: "IsSshFallback",
                unique: true,
                filter: "\"IsActive\" = true AND \"IsSshFallback\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_python_environments_Name",
                table: "python_environments",
                column: "Name",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_python_environments_Status",
                table: "python_environments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "python_environments");

            migrationBuilder.DropColumn(
                name: "PythonEnvironmentId",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "PythonEnvironmentId",
                table: "devices");
        }
    }
}
