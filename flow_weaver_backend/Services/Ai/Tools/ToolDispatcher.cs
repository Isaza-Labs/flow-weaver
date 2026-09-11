using System.Text.Json;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;

namespace flow_weaver_backend.Services.Ai.Tools;

// Scoped service that resolves + executes a tool handler for a given tool
// call. Three gates run before a handler ever sees the call:
//
//   1. Role check      — PermissionClassifier.IsAllowed (RBAC).
//   2. Autonomy tier   — human_only tools are never executed by the
//                        agent, regardless of role; the caller is told
//                        to do it from the UI.
//   3. Mutation budget — a per-scope counter of non-autonomous calls.
//                        Stops runaway loops where the agent kept calling
//                        mutations without re-checking with the user.
//
// Emits a trace.event for every dispatch so /admin/traces shows each
// tool call with its args size, success, and duration.
public sealed class ToolDispatcher
{
    // Max non-autonomous tool calls per scoped lifetime before the
    // dispatcher refuses further mutations and asks the agent to
    // surface a fresh Plan block. AiChatController creates a scope
    // per turn, so this resets naturally on each new user message.
    // Twenty covers a multi-step plan ("resolve prereq, create X, wire Y,
    // trigger Z, run W") plus a follow-up batch in the same turn; the
    // per-turn scope resets it on each new user message. Raised from 10 so
    // longer agentic batches don't stall mid-plan.
    public const int DefaultMutationBudget = 20;

    private readonly ToolRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly ICurrentUser _caller;
    private readonly ITraceLogger _trace;
    private readonly ILogger<ToolDispatcher> _logger;
    private readonly PermissionClassifier _permissions;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _settings;

    // Running count of non-autonomous calls in this scope. Scoped
    // lifetime = one request / one chat turn.
    private int _mutationsUsed;

    public ToolDispatcher(
        ToolRegistry registry,
        IServiceProvider sp,
        ICurrentUser caller,
        ITraceLogger trace,
        PermissionClassifier permissions,
        IEffectivePermissions effective,
        IAppSettingsService settings,
        ILogger<ToolDispatcher> logger)
    {
        _registry = registry;
        _sp = sp;
        _caller = caller;
        _trace = trace;
        _permissions = permissions;
        _effective = effective;
        _settings = settings;
        _logger = logger;
    }

    public int MutationBudget { get; set; } = DefaultMutationBudget;
    public int MutationsUsed => _mutationsUsed;

    // admin > operator > viewer. Falls back to the first claim (or viewer)
    // for any unrecognized role string.
    private static string HighestRole(IReadOnlyList<string> roles)
    {
        if (roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase))) return "admin";
        if (roles.Any(r => string.Equals(r, "operator", StringComparison.OrdinalIgnoreCase))) return "operator";
        return roles.FirstOrDefault() ?? "viewer";
    }

    // RBAC gate for a tool call, honouring the configured RbacMode.
    //   granular → the caller must hold the tool's mapped capability; an
    //              unmapped tool is default-denied. admin passes because
    //              CapabilitiesAsync returns the whole catalogue for admin.
    //   legacy   → keep the original PermissionClassifier role matrix so
    //              behaviour is unchanged until the mode flips over.
    // Orthogonal to this gate: the autonomy-tier (human_only) and mutation-
    // budget checks below still apply in both modes.
    private async Task<bool> IsAuthorizedAsync(string toolName, string role, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        if (!RbacModes.IsGranular(settings.RbacMode))
            return _permissions.IsAllowed(toolName, role, _caller);

        var capability = ToolCapabilityMap.For(toolName);
        if (capability is null) return false;                 // default-deny unmapped tools
        var caps = await _effective.CapabilitiesAsync(ct);
        return caps.Contains(capability);
    }

    public async Task<ToolCallOutput> DispatchAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var action = $"tool.call.{toolName}";

        var entry = _registry.Get(toolName);
        if (entry is null)
        {
            _logger.LogWarning("tool.dispatch.unknown tool={Tool}", toolName);
            await _trace.EventAsync(action, "tool", "failed",
                new { error = "unknown_tool" }, error: "unknown tool", ct: ct);
            return ToolCallOutput.Error($"unknown tool: {toolName}");
        }

        // Gate on the MOST-privileged role the caller holds, so a multi-role
        // admin (e.g. Roles = ["operator","admin"]) is evaluated as admin
        // rather than whatever happens to be first in the claim list. This is
        // what lets an admin drive admin-only config tools (create_user,
        // create_policy, …) from chat while a non-admin is refused.
        var role = HighestRole(_caller.Roles);
        if (!await IsAuthorizedAsync(toolName, role, ct))
        {
            _logger.LogWarning("tool.dispatch.denied tool={Tool} role={Role}", toolName, role);
            await _trace.EventAsync(action, "tool", "failed",
                new { error = "permission_denied", role }, error: "permission denied", ct: ct);
            return ToolCallOutput.Error($"permission denied: {toolName} (current role: {role})");
        }

        var permission = _permissions.GetPermission(toolName);

        // Resolved once and reused for the tier probe below and the actual
        // call. Handlers are scoped, so this is the same instance either way —
        // resolving twice would just be noise.
        var handler = (IToolHandler)_sp.GetRequiredService(entry.HandlerType);

        // Some tools are only dangerous for particular arguments — the matrix
        // is keyed by tool NAME and can't see that. A handler implementing
        // IArgumentSensitiveTier gets to raise its own tier for this call
        // (create_snippet does, for network_enabled=true, which lifts the
        // python sandbox's network isolation). Escalation only: a handler can
        // tighten the gate, never loosen it.
        var effectiveTier = permission.Tier;
        if (handler is IArgumentSensitiveTier sensitive)
        {
            string? escalated;
            try
            {
                escalated = sensitive.EscalatedTier(args);
            }
            catch (Exception ex)
            {
                // A handler that can't classify its own arguments must not
                // fail open — treat it as the strictest tier.
                _logger.LogError(ex, "tool.dispatch.tier_probe_failed tool={Tool}", toolName);
                escalated = PermissionClassifier.TierHumanOnly;
            }

            if (escalated is not null
                && string.Equals(escalated, PermissionClassifier.TierHumanOnly, StringComparison.OrdinalIgnoreCase))
                effectiveTier = escalated;
        }

        // Tier gate: human_only tools are ALWAYS refused by the agent
        // path. The LLM is expected to tell the user to do it from the
        // UI instead. Role alone isn't enough — some human_only ops
        // (delete_workflow) technically allow admin, but we don't want
        // the agent executing them in a chat turn.
        if (string.Equals(effectiveTier, PermissionClassifier.TierHumanOnly, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "tool.dispatch.tier_blocked tool={Tool} tier={Tier} escalated={Escalated}",
                toolName, effectiveTier, !string.Equals(effectiveTier, permission.Tier, StringComparison.Ordinal));
            await _trace.EventAsync(action, "tool", "failed",
                new { error = "tier_human_only", tier = effectiveTier, static_tier = permission.Tier },
                error: "tier is human_only — agent must not execute", ct: ct);
            return ToolCallOutput.Error(
                $"{toolName} is human_only for these arguments — the agent cannot execute it. " +
                "Tell the user to perform this action from the UI.");
        }

        // Mutation budget: every non-autonomous call spends one slot.
        // Reads, simulations, and lookups are free so the agent can
        // still gather context mid-batch without eating into the budget.
        var counts = _permissions.CountsAgainstMutationBudget(toolName);
        if (counts && _mutationsUsed >= MutationBudget)
        {
            _logger.LogWarning(
                "tool.dispatch.budget_exceeded tool={Tool} used={Used} budget={Budget}",
                toolName, _mutationsUsed, MutationBudget);
            await _trace.EventAsync(action, "tool", "failed",
                new { error = "mutation_budget_exceeded", used = _mutationsUsed, budget = MutationBudget },
                error: "mutation budget exceeded", ct: ct);
            return ToolCallOutput.Error(
                $"mutation budget exceeded ({_mutationsUsed}/{MutationBudget}). " +
                "Stop, summarize what changed so far, and ask the user to re-authorize with a fresh Plan " +
                "before calling any more mutating tools this turn.");
        }

        _logger.LogInformation(
            "tool.dispatch.begin tool={Tool} role={Role} tier={Tier} args_chars={ArgsChars} mutations_used={Used}/{Budget}",
            toolName, role, permission.Tier, args.GetRawText().Length, _mutationsUsed, MutationBudget);

        try
        {
            var result = await handler.ExecuteAsync(args, ct);
            if (counts) _mutationsUsed++;
            _logger.LogInformation(
                "tool.dispatch.end tool={Tool} elapsed_ms={Elapsed} result_chars={ResultChars} mutations_used={Used}/{Budget}",
                toolName, sw.ElapsedMilliseconds, result.GetRawText().Length, _mutationsUsed, MutationBudget);
            await _trace.EventAsync(action, "tool", "completed",
                new
                {
                    duration_ms = sw.ElapsedMilliseconds,
                    result_chars = result.GetRawText().Length,
                    tier = permission.Tier,
                    mutations_used = _mutationsUsed,
                }, ct: ct);
            return new ToolCallOutput { Success = true, Result = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "tool.dispatch.error tool={Tool} elapsed_ms={Elapsed}",
                toolName, sw.ElapsedMilliseconds);
            await _trace.EventAsync(action, "tool", "failed",
                new { duration_ms = sw.ElapsedMilliseconds, error_type = ex.GetType().Name },
                error: ex.Message, ct: ct);
            return ToolCallOutput.Error($"tool execution failed: {ex.Message}");
        }
    }
}

public sealed class ToolCallOutput
{
    public bool Success { get; init; }
    public JsonElement Result { get; init; }
    public string? ErrorMessage { get; init; }

    public static ToolCallOutput Error(string message) =>
        new()
        {
            Success = false,
            ErrorMessage = message,
            // Build the JSON via the serializer instead of string
            // interpolation so a `"` or backslash inside `message`
            // (typical when `message` is a handler exception text)
            // doesn't blow up JsonDocument.Parse and tank the dispatcher.
            Result = JsonSerializer.SerializeToElement(new { error = message }),
        };
}
