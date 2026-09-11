using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class Sprint1_MultiTenantAndAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflows",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_versions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "workflow_versions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "workflow_versions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_triggers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "workflow_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "workflow_runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_plans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "step_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "step_runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "step_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "step_runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "skills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "service_definitions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "inventory_sources",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "devices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "device_pools",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "credentials",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_providers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ai_agents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "agent_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "adapters",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "adapter_actions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "adapter_actions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "adapter_actions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "auth_events",
                columns: table => new
                {
                    AuthEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Event = table.Column<string>(type: "text", nullable: false),
                    Ip = table.Column<string>(type: "text", nullable: false),
                    UserAgent = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_events", x => x.AuthEventId);
                });

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.CompanyId);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    RefreshTokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.RefreshTokenId);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    PasswordChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MfaSecret = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflows_CompanyId",
                table: "workflows",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_CompanyId",
                table: "workflow_versions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_CompanyId",
                table: "workflow_triggers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_CompanyId",
                table: "workflow_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_plans_CompanyId",
                table: "workflow_plans",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_skills_CompanyId",
                table: "skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_service_definitions_CompanyId",
                table: "service_definitions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CompanyId",
                table: "jobs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId",
                table: "devices",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_CompanyId",
                table: "device_pools",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_agents_CompanyId",
                table: "ai_agents",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_CompanyId",
                table: "agent_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_adapters_CompanyId",
                table: "adapters",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_adapter_actions_CompanyId",
                table: "adapter_actions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_events_CompanyId",
                table: "auth_events",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_events_CompanyId_At",
                table: "auth_events",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_events_UserId_At",
                table: "auth_events",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_Slug",
                table: "companies",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_CompanyId",
                table: "refresh_tokens",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId_RevokedAt",
                table: "refresh_tokens",
                columns: new[] { "UserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_users_CompanyId",
                table: "users",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_users_CompanyId_Username",
                table: "users",
                columns: new[] { "CompanyId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_events");

            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropIndex(
                name: "IX_workflows_CompanyId",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_workflow_versions_CompanyId",
                table: "workflow_versions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_triggers_CompanyId",
                table: "workflow_triggers");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_CompanyId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_plans_CompanyId",
                table: "workflow_plans");

            migrationBuilder.DropIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs");

            migrationBuilder.DropIndex(
                name: "IX_skills_CompanyId",
                table: "skills");

            migrationBuilder.DropIndex(
                name: "IX_service_definitions_CompanyId",
                table: "service_definitions");

            migrationBuilder.DropIndex(
                name: "IX_jobs_CompanyId",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_device_pools_CompanyId",
                table: "device_pools");

            migrationBuilder.DropIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropIndex(
                name: "IX_ai_agents_CompanyId",
                table: "ai_agents");

            migrationBuilder.DropIndex(
                name: "IX_agent_runs_CompanyId",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "IX_adapters_CompanyId",
                table: "adapters");

            migrationBuilder.DropIndex(
                name: "IX_adapter_actions_CompanyId",
                table: "adapter_actions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_versions");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "workflow_versions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "workflow_versions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_triggers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_plans");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "service_definitions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "device_pools");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_providers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_agents");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "adapters");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "adapter_actions");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "adapter_actions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "adapter_actions");
        }
    }
}
