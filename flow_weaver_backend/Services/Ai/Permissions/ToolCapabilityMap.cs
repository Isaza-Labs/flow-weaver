namespace flow_weaver_backend.Services.Ai.Permissions;

// Maps each agent tool to the granular capability that authorizes it
// (plan_rbac_granular.md §6.3). In RbacMode=granular the ToolDispatcher gates a
// tool call on the caller holding this capability; an UNMAPPED tool is
// default-denied. In RbacMode=legacy the dispatcher keeps the PermissionClassifier
// role matrix instead. The autonomy tier (human_only) and the mutation budget
// stay independent of this map — they are agent-safety, not RBAC.
//
// `execute_operation` maps to `ai.chat`: it re-enters the HTTP pipeline under
// the caller's identity, so the real per-endpoint gate is that call's
// [HasPermission]; the tool itself only needs the caller to be an agent user.
public static class ToolCapabilityMap
{
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // reads
            ["list_workflows"] = "workflow.read",
            ["get_workflow"] = "workflow.read",
            ["get_workflow_details"] = "workflow.read",
            ["get_workflow_versions"] = "workflow.read",
            ["diff_workflow"] = "workflow.read",
            ["evaluate_prompt_sufficiency"] = "workflow.read",
            ["simulate_workflow_run"] = "workflow.read",
            ["list_snippets"] = "snippet.read",
            ["get_snippet"] = "snippet.read",
            ["query_devices"] = "device.read",
            ["get_device"] = "device.read",
            ["list_device_pools"] = "devicepool.read",
            ["list_integrations"] = "integration.read",
            ["get_integration"] = "integration.read",
            ["list_credentials"] = "credential.read",
            ["list_skills"] = "skill.read",
            ["load_skill"] = "skill.read",
            ["list_runs"] = "run.read",
            ["get_run"] = "run.read",
            ["get_run_steps"] = "run.read",
            ["get_run_details"] = "run.read",
            ["get_step_logs"] = "run.read",
            ["list_triggers"] = "trigger.read",
            ["get_trigger"] = "trigger.read",
            ["list_apis"] = "aicatalog.read",
            ["discover_operations"] = "aicatalog.read",
            ["operation_detail"] = "aicatalog.read",
            // Import-assist tools (TC-FW-063): read-only analysis / draft
            // generation, no persistence. Explicit mappings so they aren't
            // default-denied in granular mode.
            ["analyze_foreign_workflow"] = "workflow.read",
            ["generate_snippet_for_import"] = "snippet.read",
            ["list_reports"] = "report.read",
            ["get_report"] = "report.read",
            ["parse_file"] = "report.read",
            ["list_plan_features"] = "plan.read",
            ["validate_ssh_commands"] = "vendorcommand.read",
            ["list_vendor_commands"] = "vendorcommand.read",
            ["find_command"] = "vendorcommand.read",
            ["list_users"] = "user.read",
            ["list_policies"] = "policy.read",

            // workflow authoring / execution
            ["create_workflow"] = "workflow.create",
            ["update_workflow"] = "workflow.update",
            ["update_workflow_node_config"] = "workflow.update",
            ["mark_workflow_ready"] = "workflow.update",
            ["clone_workflow"] = "workflow.clone",
            ["delete_workflow"] = "workflow.delete",
            ["promote_workflow"] = "workflow.promote",
            ["run_workflow"] = "workflow.run",
            ["verify_workflow"] = "workflow.run",
            ["run_acceptance_tests"] = "workflow.run",

            // snippets
            ["create_snippet"] = "snippet.manage",
            ["update_snippet"] = "snippet.manage",
            ["delete_snippet"] = "snippet.manage",

            // plans / governance
            ["create_workflow_plan"] = "plan.create",
            ["update_workflow_plan"] = "plan.create",
            ["submit_plan_for_approval"] = "plan.submit",
            ["build_plan"] = "plan.build",

            // reports / integrations
            ["generate_report"] = "report.manage",
            ["integration_execute"] = "integration.execute",

            // dynamic REST — real gate is the self-call's [HasPermission].
            ["execute_operation"] = "ai.chat",

            // MCP: discovery needs mcp.read; calling a tool needs mcp.execute
            // (conditionable by server/tool — enforced contextually in the handler).
            ["list_mcp_servers"] = "mcp.read",
            ["discover_mcp_tools"] = "mcp.read",
            ["call_mcp_tool"] = "mcp.execute",

            // git
            ["git_list_repositories"] = "git.read",
            ["git_list_files"] = "git.read",
            ["git_read_file"] = "git.read",
            ["git_diff"] = "git.read",
            ["git_list_webhooks"] = "git.read",
            ["git_pull"] = "git.manage",
            ["git_write_file"] = "git.manage",
            ["git_commit_push"] = "git.manage",
            ["git_create_webhook"] = "git.manage",
            ["git_create_remote_repository"] = "git.manage",

            // app configuration
            ["create_user"] = "user.manage",
            ["set_user_role"] = "user.manage",
            ["grant_resource_permission"] = "access.manage",
            ["create_policy"] = "policy.manage",
            ["create_vendor_command"] = "vendorcommand.manage",
            ["update_vendor_command"] = "vendorcommand.manage",
            ["delete_vendor_command"] = "vendorcommand.manage",
        };

    // The capability that authorizes a tool, or null when the tool has no
    // mapping (→ default-deny in granular mode).
    public static string? For(string toolName) => Map.GetValueOrDefault(toolName);

    public static IReadOnlyCollection<string> Tools => (IReadOnlyCollection<string>)Map.Keys;
}
