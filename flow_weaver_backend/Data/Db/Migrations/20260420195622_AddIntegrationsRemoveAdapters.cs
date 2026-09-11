using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationsRemoveAdapters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Data purges ────────────────────────────────────────────────
            // Adapters are being retired entirely. Before dropping the tables
            // we rewrite every in-flight reference to them across the rest of
            // the DB so the new world boots consistent:
            //   - Workflow nodes with service_def_id="adapter_action" switch
            //     to "integration_action" (handler dispatches on this string).
            //     Same for the config_overrides key "adapter_id".
            //   - Any seeded service_definitions row that advertised
            //     "adapter_action" as its Type is removed — the Integration
            //     handler registers its own Type string at DI time.
            //   - Stored ${secret:adapter:...} tokens inside user-uploaded
            //     skills/specs become ${secret:integration:...}. The resolver
            //     stopped knowing the "adapter" source, so un-rewritten ones
            //     would silently fall back to null at runtime.
            //
            // All of these are idempotent — running the migration twice is a
            // no-op because the second pass finds nothing to rewrite.

            migrationBuilder.Sql(@"
                UPDATE workflows
                SET ""Nodes"" = REPLACE(REPLACE(""Nodes""::text,
                    '""adapter_action""', '""integration_action""'),
                    '""adapter_id""',      '""integration_id""')::jsonb
                WHERE ""Nodes""::text LIKE '%adapter%';
            ");

            migrationBuilder.Sql(@"
                DELETE FROM service_definitions WHERE ""Type"" = 'adapter_action';
            ");

            migrationBuilder.Sql(@"
                UPDATE ai_prompt_skills
                SET ""Content"" = REPLACE(""Content"", '${secret:adapter:', '${secret:integration:')
                WHERE ""Content"" LIKE '%${secret:adapter:%';
            ");

            migrationBuilder.Sql(@"
                UPDATE ai_api_specs
                SET ""Content"" = REPLACE(""Content"", '${secret:adapter:', '${secret:integration:')
                WHERE ""Content"" LIKE '%${secret:adapter:%';
            ");

            migrationBuilder.DropTable(
                name: "adapter_actions");

            migrationBuilder.DropTable(
                name: "adapters");

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "ai_prompt_skills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "ai_api_specs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "integration_actions",
                columns: table => new
                {
                    IntegrationActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    PathParams = table.Column<string>(type: "jsonb", nullable: false),
                    QueryParams = table.Column<string>(type: "jsonb", nullable: false),
                    RequestBody = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseSchema = table.Column<string>(type: "jsonb", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_actions", x => x.IntegrationActionId);
                });

            migrationBuilder.CreateTable(
                name: "integrations",
                columns: table => new
                {
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BaseURL = table.Column<string>(type: "text", nullable: false),
                    AuthConfig = table.Column<string>(type: "jsonb", nullable: false),
                    Headers = table.Column<string>(type: "jsonb", nullable: false),
                    TLSSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    HealthCheck = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrations", x => x.IntegrationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_IntegrationId",
                table: "ai_prompt_skills",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_IntegrationId",
                table: "ai_api_specs",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_actions_CompanyId",
                table: "integration_actions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_actions_IntegrationId",
                table: "integration_actions",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_CompanyId",
                table: "integrations",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_actions");

            migrationBuilder.DropTable(
                name: "integrations");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_IntegrationId",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_IntegrationId",
                table: "ai_api_specs");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "ai_api_specs");

            migrationBuilder.CreateTable(
                name: "adapter_actions",
                columns: table => new
                {
                    AdapterActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    PathParams = table.Column<string>(type: "jsonb", nullable: false),
                    QueryParams = table.Column<string>(type: "jsonb", nullable: false),
                    RequestBody = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseSchema = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adapter_actions", x => x.AdapterActionId);
                });

            migrationBuilder.CreateTable(
                name: "adapters",
                columns: table => new
                {
                    AdapterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthConfig = table.Column<string>(type: "jsonb", nullable: false),
                    BaseURL = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Headers = table.Column<string>(type: "jsonb", nullable: false),
                    HealthCheck = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TLSSkipVerify = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adapters", x => x.AdapterId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adapter_actions_AdapterId",
                table: "adapter_actions",
                column: "AdapterId");

            migrationBuilder.CreateIndex(
                name: "IX_adapter_actions_CompanyId",
                table: "adapter_actions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_adapters_CompanyId",
                table: "adapters",
                column: "CompanyId");
        }
    }
}
