using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Permissions;

// Tool permission matrix. Each tool is classified by domain + danger
// level + autonomy tier. The ToolDispatcher checks both the role-based
// level (who can call it) and the autonomy tier (how the agent should
// approach it).
//
// Levels (role gate):
//   read     — safe, no side effects
//   write    — creates/modifies data, gated by operator role
//   execute  — runs workflows or integration actions, gated by operator
//   dangerous — irreversible or external-facing, requires admin
//
// Tiers (autonomy gate — see Skills/base.md "Mutation protocol"):
//   autonomous       — read-only / simulation; no user confirm needed
//   single_confirm   — draft-scoped or reversible mutation; ONE plan + yes
//   elevated_confirm — qa writes, cross-workflow changes, promote to qa
//   human_only       — production writes, credential destruction, plan
//                      approval; agent surfaces them but cannot execute
//
// The tier is advisory for the LLM (surfaced in tool descriptions) AND
// enforced by the MutationBudget inside ToolDispatcher: multiple
// non-autonomous calls inside the same chat turn without re-authorization
// get stopped so a runaway loop can't burn through 10 mutations while
// the user is still typing a response.
//
// Registered as a singleton. The matrix itself is immutable static data;
// the instance exists so ILogger<PermissionClassifier> can be DI-injected
// and deny / classify events land in structured logs.
public sealed class PermissionClassifier
{
    public const string TierAutonomous = "autonomous";
    public const string TierSingleConfirm = "single_confirm";
    public const string TierElevatedConfirm = "elevated_confirm";
    public const string TierHumanOnly = "human_only";

    // Some entries are forward-looking: a name is declared with its intended tier
    // before any handler answers to it. They survive in the matrix because:
    //   1. The dispatcher's tool_not_found error message is more useful
    //      than the unknown→dangerous→human_only fallback would be.
    //   2. Future PRs that ship a handler don't have to remember to also
    //      update the matrix.
    // Until each gets a corresponding handler the LLM never sees it — those names
    // are not in DefaultAgentSeedService.DefaultTools either.
    //
    // WHICH ones is not written down here. It used to be, as a list in this comment,
    // and the list went stale: `create_snippet` stayed on it after its handler
    // shipped. AgentToolHandlers.RegisteredToolNames answers it from the registry, and
    // /api/ai/permissions/matrix reports `implemented` per row so an administrator
    // reading the table can tell a decision in force from an intention.
    public static readonly Dictionary<string, ToolPermission> Matrix = new(StringComparer.OrdinalIgnoreCase)
    {
        // Read-only — any authenticated user, autonomous tier.
        ["list_workflows"] = new("workflow", "read", TierAutonomous),
        ["get_workflow"] = new("workflow", "read", TierAutonomous),
        ["list_snippets"] = new("snippet", "read", TierAutonomous),
        ["get_snippet"] = new("snippet", "read", TierAutonomous),
        ["query_devices"] = new("device", "read", TierAutonomous),
        ["get_device"] = new("device", "read", TierAutonomous),
        ["list_integrations"] = new("integration", "read", TierAutonomous),
        ["get_integration"] = new("integration", "read", TierAutonomous),
        ["list_credentials"] = new("credential", "read", TierAutonomous),
        ["list_skills"] = new("skill", "read", TierAutonomous),
        // Loading the skill for the system the user is asking about is part of
        // answering, not administration: every role, no confirmation.
        ["load_skill"] = new("skill", "read", TierAutonomous),
        ["list_runs"] = new("run", "read", TierAutonomous),
        ["get_run"] = new("run", "read", TierAutonomous),
        ["get_run_steps"] = new("run", "read", TierAutonomous),
        ["get_run_details"] = new("run", "read", TierAutonomous),
        ["get_step_logs"] = new("run", "read", TierAutonomous),
        ["get_workflow_details"] = new("workflow", "read", TierAutonomous),
        ["list_triggers"] = new("trigger", "read", TierAutonomous),
        ["get_trigger"] = new("trigger", "read", TierAutonomous),
        ["list_device_pools"] = new("device", "read", TierAutonomous),
        ["get_workflow_versions"] = new("workflow", "read", TierAutonomous),
        ["diff_workflow"] = new("workflow", "read", TierAutonomous),
        ["list_apis"] = new("api", "read", TierAutonomous),
        ["discover_operations"] = new("api", "read", TierAutonomous),
        ["operation_detail"] = new("api", "read", TierAutonomous),
        ["list_reports"] = new("report", "read", TierAutonomous),
        ["get_report"] = new("report", "read", TierAutonomous),
        // Reading a file is a read, and autonomous like every other read here. Its
        // only byte source is a report artifact, and that resolves through the same
        // owner-or-admin check the download does — so this tool cannot reach a file
        // its caller could not already fetch.
        ["parse_file"] = new("report", "read", TierAutonomous),

        // Intake (FR-002/FR-003) — read level. Pure analysis, no side effects.
        ["evaluate_prompt_sufficiency"] = new("workflow", "read", TierAutonomous),

        // FR-022: static DAG validation, no handler execution.
        ["simulate_workflow_run"] = new("workflow", "read", TierAutonomous),

        // Import-assist tools (FU-3 / S15). Both are pure analysis / draft
        // generation that persist NOTHING — the wizard commits separately —
        // so read-level + autonomous, matching the other intake tools. Explicit
        // entries are REQUIRED (TC-FW-063): the unknown-fallback would tag them
        // human_only and block the wizard's own tool calls. The coverage test
        // (ToolClassificationCoverageTests) fails if either is dropped.
        ["analyze_foreign_workflow"] = new("workflow", "read", TierAutonomous),
        ["generate_snippet_for_import"] = new("snippet", "read", TierAutonomous),

        // S16 — Harness "no victory until verified" gate. Stamps a
        // ready_marker on the workflow ONLY if a fresh successful
        // simulation matches the current DAG. Role gate is `write` so a
        // read-only user cannot mark workflows ready (the handler does
        // mutate workflow.Metadata even though it never reaches devices).
        // Tier stays autonomous because there's no destructive blast
        // radius — the gate is a metadata flip the operator can undo.
        ["mark_workflow_ready"] = new("workflow", "write", TierAutonomous),

        // S16 — list of construction features tracked under a
        // WorkflowPlan or import session. Read-only; lets the agent
        // recap state when resuming a long-running task.
        ["list_plan_features"] = new("plan", "read", TierAutonomous),

        // S16 — runs the acceptance tests associated with a workflow
        // against QA Lab devices. Execute-level role gate (operator)
        // because it consumes real resources, but the agent can call it
        // mid-construction without an extra mutation budget slot.
        ["run_acceptance_tests"] = new("workflow", "execute", TierElevatedConfirm),

        // SSH-command catalog tools. All pure read/lookup against the
        // vendor_commands catalog. Without these entries the
        // unknown-fallback tags them human_only, so the dispatcher would
        // reject every call even though the handlers are registered.
        ["validate_ssh_commands"] = new("snippet", "read", TierAutonomous),
        ["list_vendor_commands"] = new("snippet", "read", TierAutonomous),
        ["find_command"] = new("snippet", "read", TierAutonomous),

        // Plans — write level, ELEVATED tier because they exist for
        // governance/review flows. Not for draft workflow creation
        // (CreateWorkflowPlanHandler now rejects draft requests).
        ["create_workflow_plan"] = new("plan", "write", TierElevatedConfirm),
        ["update_workflow_plan"] = new("plan", "write", TierElevatedConfirm),
        ["submit_plan_for_approval"] = new("plan", "write", TierElevatedConfirm),

        // Draft mutations — single_confirm. ONE plan, ONE yes, execute.
        ["create_snippet"] = new("snippet", "write", TierSingleConfirm),
        ["update_snippet"] = new("snippet", "write", TierSingleConfirm),
        ["create_workflow"] = new("workflow", "write", TierSingleConfirm),
        ["update_workflow"] = new("workflow", "write", TierSingleConfirm),
        ["update_workflow_node_config"] = new("workflow", "write", TierSingleConfirm),
        ["clone_workflow"] = new("workflow", "write", TierSingleConfirm),

        // Cross-env transitions — elevated because blast radius grows.
        ["promote_workflow"] = new("workflow", "write", TierElevatedConfirm),

        // Execution — elevated when targeting live devices; draft-only
        // runs are still single_confirm but the tool itself cannot know
        // the env up-front, so err on elevated and let the LLM explicitly
        // call out the target env in its Plan block.
        ["run_workflow"] = new("workflow", "execute", TierElevatedConfirm),
        ["verify_workflow"] = new("workflow", "execute", TierElevatedConfirm),

        // Plan-build lands the workflow for real — admin-only AND
        // human_only because it's the moment a governance plan becomes
        // a committed workflow.
        ["build_plan"] = new("plan", "execute", TierHumanOnly),

        // Reports — side-effect-bearing (persistent artifact) but
        // reversible and draft-like; single_confirm is enough.
        ["generate_report"] = new("report", "execute", TierSingleConfirm),

        // Dangerous — admin only AND human_only at the agent level.
        // Even an admin should do these from the UI, not via chat, so
        // the dispatcher refuses to execute them.
        ["integration_execute"] = new("integration", "dangerous", TierHumanOnly),
        ["delete_workflow"] = new("workflow", "dangerous", TierHumanOnly),
        ["delete_snippet"] = new("snippet", "dangerous", TierHumanOnly),

        // execute_operation covers every dynamic-spec HTTP call. Tier
        // cannot be known statically (depends on the operation's method
        // and target env) — default to single_confirm and rely on the
        // LLM's Plan block + user review. The role gate still applies.
        ["execute_operation"] = new("api", "execute", TierSingleConfirm),

        // MCP tools. Discovery is autonomous; calling a tool is single_confirm
        // (external side effects unknown — rely on the Plan block + user review).
        ["list_mcp_servers"] = new("mcp", "read", TierAutonomous),
        ["discover_mcp_tools"] = new("mcp", "read", TierAutonomous),
        ["call_mcp_tool"] = new("mcp", "execute", TierSingleConfirm),

        // Git tools. Reads (list/read/diff) are autonomous; mutations
        // (pull/write/commit/push/create_webhook) are single_confirm
        // because they're reversible (revert + force-push, delete the
        // webhook). Tiers must match what Skills/git.md tells the agent
        // to expect — keep this table in sync if either side changes.
        ["git_list_repositories"] = new("git", "read", TierAutonomous),
        ["git_list_files"] = new("git", "read", TierAutonomous),
        ["git_read_file"] = new("git", "read", TierAutonomous),
        ["git_diff"] = new("git", "read", TierAutonomous),
        ["git_list_webhooks"] = new("git", "read", TierAutonomous),
        ["git_pull"] = new("git", "write", TierSingleConfirm),
        ["git_write_file"] = new("git", "write", TierSingleConfirm),
        ["git_commit_push"] = new("git", "write", TierSingleConfirm),
        ["git_create_webhook"] = new("git", "write", TierSingleConfirm),
        // Creates a brand-new repo on GitHub via REST API + auto-registers
        // it in FlowWeaver. single_confirm because it's reversible (delete
        // the repo on GitHub, soft-delete the FW row).
        ["git_create_remote_repository"] = new("git", "write", TierSingleConfirm),

        // App configuration — the agent "configures the app" respecting the
        // requester's role. The Level is the gate: user/policy management is
        // admin-only (dangerous → admin); vendor-command edits match the
        // controller's Operator gate (write → operator|admin). The Tier is
        // elevated_confirm (execute WITH a Plan) — deliberately NOT human_only,
        // so the agent CAN run these for an authorized user. A caller who
        // lacks the Level is refused by the role gate before the handler runs.
        ["create_user"] = new("user", "dangerous", TierElevatedConfirm),
        ["list_users"] = new("user", "dangerous", TierAutonomous),
        ["set_user_role"] = new("user", "dangerous", TierElevatedConfirm),
        ["grant_resource_permission"] = new("permission", "dangerous", TierElevatedConfirm),
        ["create_policy"] = new("policy", "dangerous", TierElevatedConfirm),
        ["list_policies"] = new("policy", "read", TierAutonomous),
        ["create_vendor_command"] = new("vendor_command", "write", TierSingleConfirm),
        ["update_vendor_command"] = new("vendor_command", "write", TierSingleConfirm),
        ["delete_vendor_command"] = new("vendor_command", "write", TierSingleConfirm),
    };

    private readonly ILogger<PermissionClassifier> _logger;

    public PermissionClassifier(ILogger<PermissionClassifier> logger)
    {
        _logger = logger;
    }

    // Matrix lookup with a safe "unknown" fallback at the most
    // restrictive tier — any tool we haven't classified gets refused.
    public ToolPermission GetPermission(string toolName)
    {
        if (Matrix.TryGetValue(toolName, out var perm))
        {
            _logger.LogDebug(
                "ai.permission.classify.ok tool_name={ToolName} classification={Classification} tier={Tier}",
                toolName, perm.Level, perm.Tier);
            return perm;
        }
        _logger.LogWarning(
            "ai.permission.classify.ok tool_name={ToolName} classification=unknown tier={Tier}",
            toolName, TierHumanOnly);
        return new ToolPermission("unknown", "dangerous", TierHumanOnly);
    }

    // Role gate. Also surfaces a Warning when the caller is blocked so
    // /admin/traces has a structured signal for denied dispatches even
    // before ToolDispatcher's own audit trail picks it up.
    public bool IsAllowed(string toolName, string userRole, ICurrentUser? tenant = null)
    {
        var perm = GetPermission(toolName);
        var allowed = perm.Level switch
        {
            "read" => true,
            "write" => userRole is "admin" or "operator",
            "execute" => userRole is "admin" or "operator",
            "dangerous" => userRole == "admin",
            _ => false,
        };
        if (!allowed)
        {
            try
            {
                _logger.LogWarning(
                    "ai.permission.classify.denied tool_name={ToolName} reason={Reason} role={Role} user_id={UserId}",
                    toolName,
                    $"role_{userRole}_insufficient_for_{perm.Level}",
                    userRole,
                    tenant?.UserId);
            }
            catch (Exception ex)
            {
                // ICurrentUser accessors may throw if the scope isn't hydrated
                // (e.g. background work). Don't let a logging hiccup take
                // down the role gate itself.
                _logger.LogError(ex, "ai.permission.classify.failed tool_name={ToolName}", toolName);
            }
        }
        return allowed;
    }

    // True when the tool's tier requires a mutation-budget slot in the
    // current turn. Reads and simulations are free; every other tier
    // counts against the budget.
    public bool CountsAgainstMutationBudget(string toolName) =>
        !string.Equals(GetPermission(toolName).Tier, TierAutonomous, StringComparison.OrdinalIgnoreCase);
}

public sealed record ToolPermission(string Domain, string Level, string Tier);
