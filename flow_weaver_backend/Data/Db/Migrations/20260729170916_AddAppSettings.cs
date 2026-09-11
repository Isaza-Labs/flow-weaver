using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PermissionsGranularGatingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RbacMode = table.Column<string>(type: "text", nullable: false),
                    ImportFuzzyMatchThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ImportFuzzyMatchGap = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.Id);
                });

            // Carry the flags over from the company row that used to hold them,
            // preferring the active one and, among those, the oldest. Without
            // this the admin's saved RBAC mode would silently reset to legacy.
            migrationBuilder.Sql("""
                INSERT INTO app_settings ("Id", "PermissionsGranularGatingEnabled", "RbacMode",
                                          "ImportFuzzyMatchThreshold", "ImportFuzzyMatchGap", "UpdatedAt")
                SELECT 1, c."PermissionsGranularGatingEnabled", c."RbacMode",
                       c."ImportFuzzyMatchThreshold", c."ImportFuzzyMatchGap", now()
                FROM companies c
                ORDER BY c."IsActive" DESC, c."CreatedAt" ASC
                LIMIT 1;
                """);

            // A database with no company row at all (fresh install) still needs
            // the singleton to exist with defaults.
            migrationBuilder.Sql("""
                INSERT INTO app_settings ("Id", "PermissionsGranularGatingEnabled", "RbacMode",
                                          "ImportFuzzyMatchThreshold", "ImportFuzzyMatchGap", "UpdatedAt")
                SELECT 1, false, 'legacy', 0.8, 0.1, now()
                WHERE NOT EXISTS (SELECT 1 FROM app_settings);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");
        }
    }
}
