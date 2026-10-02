using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using AIAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Services.Ai.Seed;

// One-shot bootstrap that ensures there is a default "assistant" agent
// wired to the first enabled OpenAI provider. The AI chat controller
// falls through to this agent when no explicit agent_id is supplied, so
// without it the /ai/chat experience dead-ends at "No AI provider
// configured".
//
// Idempotent: if a Role="assistant" agent already exists (seeded or
// user-created), we leave it alone. If no enabled OpenAI provider
// exists, we skip the seed — users must configure credentials first
// through /ai/providers.
//
// Model defaults to gpt-5.4; admins can override it by editing the
// agent from /ai/agents.
public static class DefaultAgentSeedService
{
    public const string AssistantAgentName = "FlowWeaver Assistant";
    public const string AssistantRole = "assistant";
    public const string DefaultModel = "gpt-5.5";
    public const int DefaultMaxIterations = 20;
    public const double DefaultTemperature = 0.2;

    // Tool allowlist for the default agent. Names that don't exist in the
    // runtime registry are filtered out at chat time, so it's safe to list
    // forward-looking names (dynamic + domain tools landing in later
    // phases) — they'll start working as soon as the handler ships.
    private static readonly string[] DefaultTools = new[]
    {
        // Existing
        "list_workflows",
        "list_snippets",
        // In-process snippet creation (native handler). Backfilled onto
        // existing agents by BackfillMissingToolsAsync so the admin role
        // propagates for network_enabled snippets created from chat.
        "create_snippet",
        // App configuration from chat, gated per-request by the dispatcher's
        // role check: user / policy / vendor-command management.
        "create_user",
        "list_users",
        "set_user_role",
        "grant_resource_permission",
        "create_policy",
        "list_policies",
        "create_vendor_command",
        "update_vendor_command",
        "delete_vendor_command",
        "query_devices",
        "create_workflow_plan",
        // Intake clarifier. The agent calls it BEFORE
        // create_workflow_plan so missing targets / required keys turn
        // into clarifying questions instead of silent assumptions.
        "evaluate_prompt_sufficiency",
        // Dry-run a workflow (no handler execution) to catch
        // structural issues before run_workflow hits real targets.
        "simulate_workflow_run",
        // SSH command catalog: retrieve the right command for a vendor by
        // task (find_command), browse the catalog (list_vendor_commands),
        // and validate a draft (validate_ssh_commands) before saving an
        // ssh node. See Skills/ssh.md § Command decision protocol.
        "find_command",
        "list_vendor_commands",
        "validate_ssh_commands",
        // Dynamic spec tools (F2)
        "list_apis",
        "discover_operations",
        "operation_detail",
        "execute_operation",
        // MCP tools
        "list_mcp_servers",
        "discover_mcp_tools",
        "call_mcp_tool",
        // Domain debug/edit tools (F3)
        "get_run_details",
        "get_step_logs",
        "get_workflow_details",
        "update_workflow_node_config",
        // Sprint 11: report generation
        "generate_report",
        // Credentials catalog — the agent needs to resolve names like
        // "github credential" to a UUID before passing it to git_* tools.
        "list_credentials",
        // Git integration. Reads are autonomous, mutations are
        // single_confirm — see Skills/git.md for the orchestration
        // patterns the agent should follow when calling these.
        "git_list_repositories",
        "git_create_remote_repository",
        "git_list_files",
        "git_read_file",
        "git_diff",
        "git_pull",
        "git_write_file",
        "git_commit_push",
        "git_list_webhooks",
        "git_create_webhook",
    };

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // An existing assistant agent skips the create path, but we
        // still top up its Tools list with any new defaults shipped
        // since it was seeded. That way a v11 deploy gets
        // `generate_report` backfilled onto a v10 agent without
        // manual admin intervention.
        var existingAgent = await db.AIAgents
            .FirstOrDefaultAsync(a => a.IsActive
                                      && a.Role == AssistantRole);
        if (existingAgent is not null)
        {
            await BackfillMissingToolsAsync(db, existingAgent, logger);
            return;
        }

        // Only seed when there is a provider to bind to. The default agent
        // targets OpenAI (gpt-5.4); we match by Type so a deployment that
        // connects Anthropic or Ollama instead skips this and lets an admin
        // wire up their own agent.
        var provider = await db.AIProviders
            .Where(p => p.IsActive && p.Enabled
                        && p.Type == "openai")
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (provider is null) return;

        var now = DateTime.UtcNow;
        db.AIAgents.Add(new AIAgentModel
        {
            AIAgentId = Guid.NewGuid(),
            Name = AssistantAgentName,
            Role = AssistantRole,
            Description = "Default flow-weaver assistant. Uses the prompt skill catalog + dynamic API specs via tool calls.",
            ProviderId = provider.AIProviderId,
            ModelOverride = DefaultModel,
            // SystemPrompt stays empty on purpose — the base.md skill
            // carries the agent's identity and guardrails, so nothing
            // needs to live on the AIAgent row itself.
            SystemPrompt = string.Empty,
            Tools = DefaultTools.ToList(),
            MaxIterations = DefaultMaxIterations,
            Temperature = DefaultTemperature,
            Enabled = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Seeded default assistant agent on provider {ProviderId} ({Model})",
            provider.AIProviderId, DefaultModel);

    }

    // Appends any DefaultTools entry missing from an existing agent's
    // list. Preserves admin-added tools (ordering is append-only) and
    // saves only when the list actually changed.
    //
    // Caveat: if an admin explicitly removed one of the defaults via the
    // /ai/agents UI, the next boot will add it back. We accept that
    // tradeoff because the alternative — tracking "admin-removed" flags
    // — requires a schema change we don't want to ship for this. If it
    // becomes a real complaint, switch to a per-agent "SeededToolsVersion"
    // counter.
    private static async Task BackfillMissingToolsAsync(
        AppDbContext db, AIAgentModel agent, ILogger logger)
    {
        var changed = false;

        var current = agent.Tools ?? new List<string>();
        var missing = DefaultTools
            .Where(name => !current.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            var next = new List<string>(current);
            next.AddRange(missing);
            agent.Tools = next;
            changed = true;
            logger.LogInformation(
                "Backfilled {Count} default tool(s) onto existing assistant {AgentId}: {Tools}",
                missing.Count, agent.AIAgentId, string.Join(", ", missing));
        }

        // Raise the recursion cap toward the shipped default so agents seeded
        // before the bump get the roomier loop. Only ever raises — never
        // lowers a value an admin set higher on purpose.
        if (agent.MaxIterations < DefaultMaxIterations)
        {
            logger.LogInformation(
                "Raised MaxIterations {Old}->{New} on existing assistant {AgentId}",
                agent.MaxIterations, DefaultMaxIterations, agent.AIAgentId);
            agent.MaxIterations = DefaultMaxIterations;
            changed = true;
        }

        if (!changed) return;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
