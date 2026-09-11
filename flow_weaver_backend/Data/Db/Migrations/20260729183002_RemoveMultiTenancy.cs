using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace flow_weaver_backend.Data.Db.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preconditions. Every unique index below used to be scoped by
            // CompanyId; dropping that column collapses each one to its
            // remaining columns. On a database that ever held more than one
            // company those columns can legitimately collide, and the
            // CreateIndex calls further down would fail with a bare
            // "duplicate key" that says nothing about which rows to fix.
            // Fail here instead, naming the table, so the operator can
            // deduplicate before retrying. See company_remove.md §F0.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    dup_count bigint;
                    offender  text;
                BEGIN
                    FOR offender, dup_count IN
                        SELECT 'users("Username")', count(*)
                            FROM (SELECT "Username" FROM users
                                  GROUP BY "Username" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'secrets("Name")', count(*)
                            FROM (SELECT "Name" FROM secrets
                                  GROUP BY "Name" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'allowed_python_modules("ImportName")', count(*)
                            FROM (SELECT "ImportName" FROM allowed_python_modules
                                  GROUP BY "ImportName" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'ai_prompt_skills("Name")', count(*)
                            FROM (SELECT "Name" FROM ai_prompt_skills
                                  GROUP BY "Name" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'ai_api_specs("Api")', count(*)
                            FROM (SELECT "Api" FROM ai_api_specs
                                  GROUP BY "Api" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'git_repositories("Name")', count(*)
                            FROM (SELECT "Name" FROM git_repositories
                                  WHERE "IsActive" GROUP BY "Name" HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'agent_scratches("ConversationId","Key")', count(*)
                            FROM (SELECT "ConversationId", "Key" FROM agent_scratches
                                  WHERE "IsActive" GROUP BY "ConversationId", "Key"
                                  HAVING count(*) > 1) d
                        UNION ALL
                        SELECT 'mcp_tools("McpServerId","Name")', count(*)
                            FROM (SELECT "McpServerId", "Name" FROM mcp_tools
                                  WHERE "IsActive" GROUP BY "McpServerId", "Name"
                                  HAVING count(*) > 1) d
                    LOOP
                        IF dup_count > 0 THEN
                            RAISE EXCEPTION
                                'RemoveMultiTenancy: % has % duplicate group(s) that were only unique per company. Deduplicate before migrating (see company_remove.md).',
                                offender, dup_count;
                        END IF;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "companies");

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
                name: "IX_workflow_acceptance_tests_CompanyId",
                table: "workflow_acceptance_tests");

            migrationBuilder.DropIndex(
                name: "IX_workflow_acceptance_tests_CompanyId_LastStatus",
                table: "workflow_acceptance_tests");

            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_CompanyId",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_CompanyId_DeviceType_Kind",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_CompanyId_DeviceType_Kind_Value",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_users_CompanyId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_CompanyId_Username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_trace_events_CompanyId",
                table: "trace_events");

            migrationBuilder.DropIndex(
                name: "IX_trace_events_CompanyId_At",
                table: "trace_events");

            migrationBuilder.DropIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs");

            migrationBuilder.DropIndex(
                name: "IX_snippets_CompanyId",
                table: "snippets");

            migrationBuilder.DropIndex(
                name: "IX_skills_CompanyId",
                table: "skills");

            migrationBuilder.DropIndex(
                name: "IX_simulation_results_CompanyId",
                table: "simulation_results");

            migrationBuilder.DropIndex(
                name: "IX_secrets_CompanyId",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_secrets_CompanyId_Name",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_resource_permissions_CompanyId",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "IX_resource_permissions_CompanyId_ResourceType_ResourceId",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "IX_resource_permissions_CompanyId_SubjectType_SubjectId",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "UX_resource_permissions_unique_grant",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CompanyId",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CompanyId_CreatedAt",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CompanyId_Format",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CompanyId_Source",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CompanyId_UserId_CreatedAt",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_CompanyId",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_policies_CompanyId",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "IX_policies_CompanyId_Enabled",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "IX_plan_features_CompanyId",
                table: "plan_features");

            migrationBuilder.DropIndex(
                name: "IX_plan_features_CompanyId_Status",
                table: "plan_features");

            migrationBuilder.DropIndex(
                name: "IX_permission_grants_CompanyId",
                table: "permission_grants");

            migrationBuilder.DropIndex(
                name: "IX_permission_grants_CompanyId_Enabled",
                table: "permission_grants");

            migrationBuilder.DropIndex(
                name: "IX_permission_grants_CompanyId_Name",
                table: "permission_grants");

            migrationBuilder.DropIndex(
                name: "IX_messaging_link_tokens_CompanyId",
                table: "messaging_link_tokens");

            migrationBuilder.DropIndex(
                name: "IX_messaging_inbound_events_CompanyId",
                table: "messaging_inbound_events");

            migrationBuilder.DropIndex(
                name: "IX_messaging_inbound_events_CompanyId_At",
                table: "messaging_inbound_events");

            migrationBuilder.DropIndex(
                name: "IX_messaging_identity_links_CompanyId",
                table: "messaging_identity_links");

            migrationBuilder.DropIndex(
                name: "IX_messaging_deliveries_CompanyId",
                table: "messaging_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_messaging_deliveries_CompanyId_At",
                table: "messaging_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_messaging_channels_CompanyId",
                table: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "IX_messaging_channels_CompanyId_Provider",
                table: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "IX_mcp_tools_CompanyId",
                table: "mcp_tools");

            migrationBuilder.DropIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId",
                table: "mcp_tools");

            migrationBuilder.DropIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId_Name",
                table: "mcp_tools");

            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_CompanyId",
                table: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_CompanyId_Enabled",
                table: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "IX_jobs_CompanyId",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropIndex(
                name: "IX_integrations_CompanyId",
                table: "integrations");

            migrationBuilder.DropIndex(
                name: "IX_integration_actions_CompanyId",
                table: "integration_actions");

            migrationBuilder.DropIndex(
                name: "IX_git_webhooks_CompanyId",
                table: "git_webhooks");

            migrationBuilder.DropIndex(
                name: "IX_git_webhook_deliveries_CompanyId",
                table: "git_webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_CompanyId",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_CompanyId_Name",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_devices_CompanyId_SourceId_ExternalId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_device_pools_CompanyId",
                table: "device_pools");

            migrationBuilder.DropIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_auth_events_CompanyId",
                table: "auth_events");

            migrationBuilder.DropIndex(
                name: "IX_auth_events_CompanyId_At",
                table: "auth_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_EntityId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_EntityType_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CompanyId_UserId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_allowed_python_modules_CompanyId",
                table: "allowed_python_modules");

            migrationBuilder.DropIndex(
                name: "IX_allowed_python_modules_CompanyId_ImportName",
                table: "allowed_python_modules");

            migrationBuilder.DropIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_CompanyId_IsActive_SortOrder_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_CompanyId_IsActive",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_agents_CompanyId",
                table: "ai_agents");

            migrationBuilder.DropIndex(
                name: "IX_agent_scratches_CompanyId",
                table: "agent_scratches");

            migrationBuilder.DropIndex(
                name: "IX_agent_scratches_CompanyId_ConversationId_Key",
                table: "agent_scratches");

            migrationBuilder.DropIndex(
                name: "IX_agent_runs_CompanyId",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_versions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_triggers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_plans");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "workflow_acceptance_tests");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "vendor_commands");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "trace_events");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "step_runs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "snippets");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "simulation_results");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "resource_permissions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "report_artifacts");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "policies");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "plan_features");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "permission_grants");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "messaging_link_tokens");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "messaging_inbound_events");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "messaging_identity_links");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "messaging_deliveries");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "messaging_channels");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "mcp_tools");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "inventory_sources");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "integration_actions");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "git_webhooks");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "git_webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "git_repositories");

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
                table: "auth_events");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "allowed_python_modules");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_providers");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_prompt_skills");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_conversations");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_api_specs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ai_agents");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "agent_scratches");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "agent_runs");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_LastStatus",
                table: "workflow_acceptance_tests",
                column: "LastStatus");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_DeviceType_Kind",
                table: "vendor_commands",
                columns: new[] { "DeviceType", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commands_DeviceType_Kind_Value",
                table: "vendor_commands",
                columns: new[] { "DeviceType", "Kind", "Value" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_At",
                table: "trace_events",
                column: "At",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Name",
                table: "secrets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_permissions_ResourceType_ResourceId",
                table: "resource_permissions",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_permissions_SubjectType_SubjectId",
                table: "resource_permissions",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_resource_permissions_unique_grant",
                table: "resource_permissions",
                columns: new[] { "ResourceType", "ResourceId", "SubjectType", "SubjectId", "Role" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CreatedAt",
                table: "report_artifacts",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_Format",
                table: "report_artifacts",
                column: "Format");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_Source",
                table: "report_artifacts",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_UserId_CreatedAt",
                table: "report_artifacts",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_policies_Enabled",
                table: "policies",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_Status",
                table: "plan_features",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_Enabled",
                table: "permission_grants",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_Name",
                table: "permission_grants",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_At",
                table: "messaging_inbound_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_At",
                table: "messaging_deliveries",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_Provider",
                table: "messaging_channels",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_McpServerId",
                table: "mcp_tools",
                column: "McpServerId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_McpServerId_Name",
                table: "mcp_tools",
                columns: new[] { "McpServerId", "Name" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_Enabled",
                table: "mcp_servers",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories",
                column: "Name",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_At",
                table: "audit_logs",
                column: "At",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityId_At",
                table: "audit_logs",
                columns: new[] { "EntityId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityType_At",
                table: "audit_logs",
                columns: new[] { "EntityType", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_UserId_At",
                table: "audit_logs",
                columns: new[] { "UserId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_ImportName",
                table: "allowed_python_modules",
                column: "ImportName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_IsActive_SortOrder_Name",
                table: "ai_prompt_skills",
                columns: new[] { "IsActive", "SortOrder", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs",
                column: "Api",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_IsActive",
                table: "ai_api_specs",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_ConversationId_Key",
                table: "agent_scratches",
                columns: new[] { "ConversationId", "Key" },
                unique: true,
                filter: "\"IsActive\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_acceptance_tests_LastStatus",
                table: "workflow_acceptance_tests");

            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_DeviceType_Kind",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_vendor_commands_DeviceType_Kind_Value",
                table: "vendor_commands");

            migrationBuilder.DropIndex(
                name: "IX_users_Username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_trace_events_At",
                table: "trace_events");

            migrationBuilder.DropIndex(
                name: "IX_secrets_Name",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_resource_permissions_ResourceType_ResourceId",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "IX_resource_permissions_SubjectType_SubjectId",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "UX_resource_permissions_unique_grant",
                table: "resource_permissions");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_CreatedAt",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_Format",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_Source",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_report_artifacts_UserId_CreatedAt",
                table: "report_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_policies_Enabled",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "IX_plan_features_Status",
                table: "plan_features");

            migrationBuilder.DropIndex(
                name: "IX_permission_grants_Enabled",
                table: "permission_grants");

            migrationBuilder.DropIndex(
                name: "IX_permission_grants_Name",
                table: "permission_grants");

            migrationBuilder.DropIndex(
                name: "IX_messaging_inbound_events_At",
                table: "messaging_inbound_events");

            migrationBuilder.DropIndex(
                name: "IX_messaging_deliveries_At",
                table: "messaging_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_messaging_channels_Provider",
                table: "messaging_channels");

            migrationBuilder.DropIndex(
                name: "IX_mcp_tools_McpServerId",
                table: "mcp_tools");

            migrationBuilder.DropIndex(
                name: "IX_mcp_tools_McpServerId_Name",
                table: "mcp_tools");

            migrationBuilder.DropIndex(
                name: "IX_mcp_servers_Enabled",
                table: "mcp_servers");

            migrationBuilder.DropIndex(
                name: "IX_git_repositories_Name",
                table: "git_repositories");

            migrationBuilder.DropIndex(
                name: "IX_devices_SourceId_ExternalId",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_EntityId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_EntityType_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_UserId_At",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_allowed_python_modules_ImportName",
                table: "allowed_python_modules");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_IsActive_SortOrder_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_prompt_skills_Name",
                table: "ai_prompt_skills");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_Api",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_ai_api_specs_IsActive",
                table: "ai_api_specs");

            migrationBuilder.DropIndex(
                name: "IX_agent_scratches_ConversationId_Key",
                table: "agent_scratches");

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

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_plans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "workflow_acceptance_tests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "vendor_commands",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "trace_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "step_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "snippets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "skills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "simulation_results",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "secrets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "resource_permissions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "report_artifacts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "policies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "plan_features",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "permission_grants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "messaging_link_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "messaging_inbound_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "messaging_identity_links",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "messaging_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "messaging_channels",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "mcp_tools",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "mcp_servers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "inventory_sources",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "integrations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "integration_actions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "git_webhooks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "git_webhook_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "git_repositories",
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
                table: "auth_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "audit_logs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "allowed_python_modules",
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
                table: "ai_prompt_skills",
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
                table: "ai_api_specs",
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
                table: "agent_scratches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "agent_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportFuzzyMatchGap = table.Column<double>(type: "double precision", nullable: false),
                    ImportFuzzyMatchThreshold = table.Column<double>(type: "double precision", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PermissionsGranularGatingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RbacMode = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.CompanyId);
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
                name: "IX_workflow_acceptance_tests_CompanyId",
                table: "workflow_acceptance_tests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_acceptance_tests_CompanyId_LastStatus",
                table: "workflow_acceptance_tests",
                columns: new[] { "CompanyId", "LastStatus" });

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
                name: "IX_trace_events_CompanyId",
                table: "trace_events",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_trace_events_CompanyId_At",
                table: "trace_events",
                columns: new[] { "CompanyId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_step_runs_CompanyId",
                table: "step_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_snippets_CompanyId",
                table: "snippets",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_skills_CompanyId",
                table: "skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_results_CompanyId",
                table: "simulation_results",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_CompanyId",
                table: "secrets",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_CompanyId_Name",
                table: "secrets",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId",
                table: "report_artifacts",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_CreatedAt",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_Format",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "Format" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_Source",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_report_artifacts_CompanyId_UserId_CreatedAt",
                table: "report_artifacts",
                columns: new[] { "CompanyId", "UserId", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_CompanyId",
                table: "refresh_tokens",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_policies_CompanyId",
                table: "policies",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_policies_CompanyId_Enabled",
                table: "policies",
                columns: new[] { "CompanyId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_CompanyId",
                table: "plan_features",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_CompanyId_Status",
                table: "plan_features",
                columns: new[] { "CompanyId", "Status" });

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

            migrationBuilder.CreateIndex(
                name: "IX_messaging_link_tokens_CompanyId",
                table: "messaging_link_tokens",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_CompanyId",
                table: "messaging_inbound_events",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_inbound_events_CompanyId_At",
                table: "messaging_inbound_events",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_identity_links_CompanyId",
                table: "messaging_identity_links",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_CompanyId",
                table: "messaging_deliveries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_deliveries_CompanyId_At",
                table: "messaging_deliveries",
                columns: new[] { "CompanyId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_CompanyId",
                table: "messaging_channels",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_messaging_channels_CompanyId_Provider",
                table: "messaging_channels",
                columns: new[] { "CompanyId", "Provider" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId",
                table: "mcp_tools",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId",
                table: "mcp_tools",
                columns: new[] { "CompanyId", "McpServerId" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tools_CompanyId_McpServerId_Name",
                table: "mcp_tools",
                columns: new[] { "CompanyId", "McpServerId", "Name" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_CompanyId",
                table: "mcp_servers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_servers_CompanyId_Enabled",
                table: "mcp_servers",
                columns: new[] { "CompanyId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_CompanyId",
                table: "jobs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_sources_CompanyId",
                table: "inventory_sources",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_CompanyId",
                table: "integrations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_actions_CompanyId",
                table: "integration_actions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_webhooks_CompanyId",
                table: "git_webhooks",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_webhook_deliveries_CompanyId",
                table: "git_webhook_deliveries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_CompanyId",
                table: "git_repositories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_git_repositories_CompanyId_Name",
                table: "git_repositories",
                columns: new[] { "CompanyId", "Name" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId",
                table: "devices",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CompanyId_SourceId_ExternalId",
                table: "devices",
                columns: new[] { "CompanyId", "SourceId", "ExternalId" },
                unique: true,
                filter: "\"SourceId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_device_pools_CompanyId",
                table: "device_pools",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_CompanyId",
                table: "credentials",
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
                name: "IX_audit_logs_CompanyId",
                table: "audit_logs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "At" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_EntityId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "EntityId", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_EntityType_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "EntityType", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CompanyId_UserId_At",
                table: "audit_logs",
                columns: new[] { "CompanyId", "UserId", "At" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_CompanyId",
                table: "allowed_python_modules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_allowed_python_modules_CompanyId_ImportName",
                table: "allowed_python_modules",
                columns: new[] { "CompanyId", "ImportName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_providers_CompanyId",
                table: "ai_providers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId",
                table: "ai_prompt_skills",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_IsActive_SortOrder_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "IsActive", "SortOrder", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_skills_CompanyId_Name",
                table: "ai_prompt_skills",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_conversations_CompanyId",
                table: "ai_conversations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId",
                table: "ai_api_specs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_Api",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "Api" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_specs_CompanyId_IsActive",
                table: "ai_api_specs",
                columns: new[] { "CompanyId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_agents_CompanyId",
                table: "ai_agents",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_CompanyId",
                table: "agent_scratches",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_scratches_CompanyId_ConversationId_Key",
                table: "agent_scratches",
                columns: new[] { "CompanyId", "ConversationId", "Key" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_CompanyId",
                table: "agent_runs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_companies_Slug",
                table: "companies",
                column: "Slug",
                unique: true);
        }
    }
}
