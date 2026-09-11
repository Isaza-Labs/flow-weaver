namespace flow_weaver_backend.Services.Permission.Catalog;

// The single source of truth for every granular capability the system can
// enforce. This is phase 0 of the RBAC-granular refactor (plan_rbac_granular.md):
// it introduces NO runtime behaviour on its own — later phases consume it
//   • the resolver (IEffectivePermissions) checks a caller's grants against it,
//   • the [HasPermission] attribute names a Key,
//   • the seeder turns CapabilitiesForLegacyRole(...) into builtin.* grants,
//   • the agent's tool→capability map targets these Keys.
//
// Compatibility is embedded per entry (Capability.LegacyTier) rather than kept
// in a separate matrix, so the "which legacy role implies this" mapping can
// never drift from the catalogue.
//
// Adding a capability: pick `domain.action`, the tightest ContextDimensions
// that make sense, and the LegacyTier that reproduces today's gate for the
// endpoint(s) it will protect. CapabilityCatalogTests guards the invariants.
public static class CapabilityCatalog
{
    private const ContextDimensions None = ContextDimensions.None;
    private const ContextDimensions Env = ContextDimensions.Environment;
    private const ContextDimensions Dev = ContextDimensions.Device;
    private const ContextDimensions Res = ContextDimensions.Resource;
    private const ContextDimensions Mcp = ContextDimensions.Mcp;

    private static Capability Cap(
        string key, string domain, LegacyTier tier, ContextDimensions dims, string desc)
        => new(key, domain, desc, dims, tier);

    public static readonly IReadOnlyList<Capability> All = new[]
    {
        // ── workflow ──────────────────────────────────────────────────────
        Cap("workflow.read",     "workflow", LegacyTier.Viewer,   Res,             "View workflows, versions and diffs."),
        Cap("workflow.create",   "workflow", LegacyTier.Operator, Env | Res,       "Create a workflow."),
        Cap("workflow.update",   "workflow", LegacyTier.Operator, Env | Res,       "Edit a workflow's nodes/config."),
        Cap("workflow.delete",   "workflow", LegacyTier.Operator, Res,             "Delete a workflow."),
        Cap("workflow.clone",    "workflow", LegacyTier.Operator, Res,             "Clone a workflow."),
        Cap("workflow.import",   "workflow", LegacyTier.Operator, None,            "Import workflows from an external definition."),
        Cap("workflow.run",      "workflow", LegacyTier.Operator, Env | Dev | Res, "Run a workflow against its target environment/devices."),
        Cap("workflow.promote",  "workflow", LegacyTier.Operator, Env | Res,       "Promote a workflow across environments (draft→qa→production)."),
        Cap("workflow.rollback", "workflow", LegacyTier.Operator, Env | Res,       "Roll a workflow back to a previous version/environment."),

        // ── run (execution history) ───────────────────────────────────────
        Cap("run.read",   "run", LegacyTier.Viewer, None, "View workflow runs and their step runs."),
        Cap("run.cancel", "run", LegacyTier.Viewer, None, "Cancel an in-flight run (whoever can launch can stop)."),
        Cap("run.delete", "run", LegacyTier.Admin,  None, "Delete/prune run history."),

        // ── plan (governance) ─────────────────────────────────────────────
        Cap("plan.read",    "plan", LegacyTier.Viewer,   None, "View workflow plans and their features."),
        Cap("plan.create",  "plan", LegacyTier.Operator, None, "Create a workflow plan."),
        Cap("plan.submit",  "plan", LegacyTier.Operator, None, "Submit a plan for approval."),
        Cap("plan.approve", "plan", LegacyTier.Admin,    None, "Approve or reject a submitted plan."),
        Cap("plan.build",   "plan", LegacyTier.Admin,    None, "Build an approved plan into a committed workflow."),

        // ── triggers / schedules ──────────────────────────────────────────
        Cap("trigger.read",   "trigger", LegacyTier.Viewer,   None, "View workflow triggers/schedules."),
        Cap("trigger.manage", "trigger", LegacyTier.Operator, None, "Create/edit/delete triggers and schedules."),

        // ── devices / connectivity ────────────────────────────────────────
        Cap("device.read",        "device", LegacyTier.Viewer,   Dev,       "Query devices."),
        Cap("device.manage",      "device", LegacyTier.Operator, None,      "Create/edit/delete devices."),
        // New granular execution primitives — today an operator running a
        // workflow implicitly executes device commands, so they map to Operator
        // for behaviour parity. Phase 7 enforces them per-command in SshHandler
        // with the concrete device role/pool/env in context.
        Cap("device.exec.read",  "device", LegacyTier.Operator, Env | Dev, "Run read-only/show commands on a device."),
        Cap("device.exec.write", "device", LegacyTier.Operator, Env | Dev, "Run configuration/mutating commands on a device."),

        Cap("devicepool.read",   "devicepool", LegacyTier.Viewer,   None, "View device pools."),
        Cap("devicepool.manage", "devicepool", LegacyTier.Operator, None, "Create/edit/delete device pools."),

        Cap("inventory.read",   "inventory", LegacyTier.Viewer,   None, "View inventory sources."),
        Cap("inventory.manage", "inventory", LegacyTier.Operator, None, "Create/edit/delete inventory sources."),

        Cap("credential.read",   "credential", LegacyTier.Viewer,   None, "View credentials (metadata only)."),
        Cap("credential.manage", "credential", LegacyTier.Operator, None, "Create/edit/delete credentials."),

        Cap("secret.read",   "secret", LegacyTier.Admin, None, "Read secret values."),
        Cap("secret.manage", "secret", LegacyTier.Admin, None, "Create/edit/delete secrets."),

        // ── integrations ──────────────────────────────────────────────────
        Cap("integration.read",    "integration", LegacyTier.Viewer,   Res,       "View integrations."),
        Cap("integration.manage",  "integration", LegacyTier.Operator, Res,       "Create/edit/delete integrations."),
        Cap("integration.test",    "integration", LegacyTier.Operator, Env | Res, "Test-fire an integration action (live HTTP)."),
        Cap("integration.execute", "integration", LegacyTier.Operator, Env | Res, "Execute an integration action as part of a run."),

        Cap("integrationaction.read",   "integrationaction", LegacyTier.Viewer,   None, "View integration actions."),
        Cap("integrationaction.manage", "integrationaction", LegacyTier.Operator, None, "Create/edit/delete integration actions."),

        // ── MCP servers ───────────────────────────────────────────────────
        Cap("mcpserver.read",   "mcpserver", LegacyTier.Viewer, None, "View MCP servers and their discovered tools."),
        Cap("mcpserver.manage", "mcpserver", LegacyTier.Admin,  None, "Create/edit/delete MCP servers (holds connection secrets)."),

        Cap("mcp.read",    "mcp", LegacyTier.Viewer,   None, "Discover MCP tools (list servers, browse cached tools)."),
        Cap("mcp.execute", "mcp", LegacyTier.Operator, Mcp,  "Call an MCP tool — conditionable by server and by tool."),

        // ── snippets / skills / vendor commands ───────────────────────────
        Cap("snippet.read",   "snippet", LegacyTier.Viewer,   None, "View snippets."),
        Cap("snippet.manage", "snippet", LegacyTier.Operator, None, "Create/edit/delete snippets."),

        Cap("skill.read",   "skill", LegacyTier.Viewer,   None, "View skills."),
        Cap("skill.manage", "skill", LegacyTier.Operator, None, "Create/edit/delete skills."),

        Cap("vendorcommand.read",   "vendorcommand", LegacyTier.Viewer,   None, "View the vendor-command catalog."),
        Cap("vendorcommand.manage", "vendorcommand", LegacyTier.Operator, None, "Create/edit/delete vendor commands."),

        // ── governance: policies + access (the RBAC surface itself) ───────
        Cap("policy.read",   "policy", LegacyTier.Viewer, None, "View corporate policies and the policy audit."),
        Cap("policy.manage", "policy", LegacyTier.Admin,  None, "Create/edit/delete corporate policies."),

        Cap("access.read",   "access", LegacyTier.Viewer, Res,  "View who has access to a resource."),
        Cap("access.manage", "access", LegacyTier.Admin,  Res,  "Grant/revoke permissions and manage permission grants."),

        // ── AI ────────────────────────────────────────────────────────────
        Cap("ai.chat",             "ai", LegacyTier.Viewer, None, "Use the AI agent (chat)."),
        Cap("ai.permissions.read", "ai", LegacyTier.Admin,  None, "View the AI tool permission matrix."),

        Cap("conversations.read", "conversations", LegacyTier.Viewer, None, "View/delete one's own AI conversations."),

        Cap("aicatalog.read",   "aicatalog", LegacyTier.Viewer, None, "Read the agent catalog (system prompt + operations)."),
        Cap("aicatalog.reload", "aicatalog", LegacyTier.Admin,  None, "Reload/invalidate the agent catalog."),

        Cap("aiagent.read",   "aiagent", LegacyTier.Viewer,   None, "View AI agents."),
        Cap("aiagent.manage", "aiagent", LegacyTier.Operator, None, "Create/edit/delete AI agents."),

        Cap("aiprovider.read",   "aiprovider", LegacyTier.Viewer, None, "View AI providers."),
        Cap("aiprovider.manage", "aiprovider", LegacyTier.Admin,  None, "Create/edit/delete AI providers (API keys)."),

        Cap("promptskill.read",   "promptskill", LegacyTier.Admin, None, "View AI prompt skills."),
        Cap("promptskill.manage", "promptskill", LegacyTier.Admin, None, "Create/edit/delete AI prompt skills."),

        Cap("apispec.read",   "apispec", LegacyTier.Admin, None, "View AI API specs."),
        Cap("apispec.manage", "apispec", LegacyTier.Admin, None, "Create/edit/delete AI API specs."),

        Cap("pythonmodule.read",   "pythonmodule", LegacyTier.Admin, None, "View the allowed Python module list."),
        Cap("pythonmodule.manage", "pythonmodule", LegacyTier.Admin, None, "Manage the Python sandbox import allowlist."),

        // ── git ───────────────────────────────────────────────────────────
        Cap("git.read",   "git", LegacyTier.Viewer, None, "Read git repositories (list/read/diff)."),
        Cap("git.manage", "git", LegacyTier.Admin,  None, "Git write ops (pull/write/commit/push, remote/webhook creation)."),

        Cap("gitwebhook.read",   "gitwebhook", LegacyTier.Viewer, None, "View git webhook registrations."),
        Cap("gitwebhook.manage", "gitwebhook", LegacyTier.Admin,  None, "Create/edit/delete git webhook registrations."),

        // ── messaging channels ────────────────────────────────────────────
        Cap("messaging.link",   "messaging", LegacyTier.Viewer, None, "Link/unlink one's own external messaging identity."),
        Cap("messaging.manage", "messaging", LegacyTier.Admin,  None, "Manage messaging channels, tokens and links."),

        // ── outbound email (SMTP channels) ────────────────────────────────
        // A channel holds an SMTP credential, so managing one is Admin — same
        // tier as messaging.manage and mcpserver.manage. Sending is Operator:
        // it is the runtime half, exercised by an email_send step, and is
        // conditionable per resource so a policy can pin a channel to a team.
        Cap("email.read",   "email", LegacyTier.Viewer,   Res, "View email (SMTP) channels."),
        Cap("email.manage", "email", LegacyTier.Admin,    Res, "Create/edit/delete email channels (holds SMTP credentials)."),
        Cap("email.send",   "email", LegacyTier.Operator, Res, "Send an email through a channel (test-send or an email_send step)."),

        // ── reports ───────────────────────────────────────────────────────
        Cap("report.read",   "report", LegacyTier.Viewer, None, "View reports."),
        Cap("report.manage", "report", LegacyTier.Admin,  None, "Generate/edit/delete reports."),

        // ── observability dashboards ──────────────────────────────────────
        Cap("qalab.read",   "qalab",   LegacyTier.Viewer, None, "View the QA lab dashboard."),
        Cap("job.stats.read", "job",   LegacyTier.Viewer, None, "View queue stats (counts only)."),
        Cap("job.read",     "job",     LegacyTier.Admin,  None, "View queue job detail."),
        Cap("audit.read",   "audit",   LegacyTier.Admin,  None, "Read the audit log."),
        Cap("trace.read",   "trace",   LegacyTier.Admin,  None, "Read trace events."),
        Cap("metrics.read", "metrics", LegacyTier.Admin,  None, "Read admin metrics / SLO."),

        // ── administration ────────────────────────────────────────────────
        Cap("user.read",     "user",    LegacyTier.Admin, None, "View users."),
        Cap("user.manage",   "user",    LegacyTier.Admin, None, "Create users and set user roles."),
        Cap("settings.read",   "settings", LegacyTier.Admin, None, "View deployment settings."),
        Cap("settings.manage", "settings", LegacyTier.Admin, None, "Change deployment settings."),
    };

    private static readonly IReadOnlyDictionary<string, Capability> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    public static Capability? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)ByKey.Keys;

    public static IReadOnlyList<string> Domains =>
        All.Select(c => c.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Capability keys implied by a legacy role, honouring the tier ladder
    // (viewer ⊂ operator). Used by the phase-1 seeder to build the built-in
    // grants that reproduce today's behaviour. `admin` returns everything as a
    // documented convenience, but admin bypasses all checks and is not seeded.
    public static IReadOnlyList<string> CapabilitiesForLegacyRole(string role) =>
        role.ToLowerInvariant() switch
        {
            "viewer" => All.Where(c => c.LegacyTier == LegacyTier.Viewer)
                           .Select(c => c.Key).ToList(),
            "operator" => All.Where(c => c.LegacyTier is LegacyTier.Viewer or LegacyTier.Operator)
                             .Select(c => c.Key).ToList(),
            "admin" => All.Select(c => c.Key).ToList(),
            _ => Array.Empty<string>(),
        };
}
