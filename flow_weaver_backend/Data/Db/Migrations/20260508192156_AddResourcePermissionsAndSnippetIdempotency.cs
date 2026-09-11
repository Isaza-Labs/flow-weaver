using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddResourcePermissionsAndSnippetIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Idempotency",
                table: "snippets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resource_permissions",
                columns: table => new
                {
                    ResourcePermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectType = table.Column<string>(type: "text", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_permissions", x => x.ResourcePermissionId);
                    table.CheckConstraint("CK_resource_permissions_ResourceType", "\"ResourceType\" IN ('workflow', 'integration')");
                    table.CheckConstraint("CK_resource_permissions_Role", "\"Role\" IN ('owner', 'editor', 'runner', 'viewer')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_snippets_Idempotency",
                table: "snippets",
                sql: "\"Idempotency\" IS NULL OR \"Idempotency\" IN ('idempotent', 'requires_compensation', 'non_reversible')");

            migrationBuilder.CreateIndex(
                name: "IX_resource_permissions_CompanyId",
                table: "resource_permissions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_resource_permissions_CompanyId_ResourceType_ResourceId",
                table: "resource_permissions",
                columns: new[] { "CompanyId", "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_permissions_CompanyId_SubjectType_SubjectId",
                table: "resource_permissions",
                columns: new[] { "CompanyId", "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_resource_permissions_unique_grant",
                table: "resource_permissions",
                columns: new[] { "CompanyId", "ResourceType", "ResourceId", "SubjectType", "SubjectId", "Role" },
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_permissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_snippets_Idempotency",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "Idempotency",
                table: "snippets");
        }
    }
}
