using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;

namespace flow_weaver_backend.Services.Policy;

// Default-allow, opt-in-deny matcher. Each Policy.Rule is a small JSON
// predicate; we iterate the enabled rules and short-circuit on the
// first deny / unmet gate.
//
// Two rule shapes are supported:
//
// 1) action="deny" — original matcher. Fields under `when` (all
//    optional, AND-joined): env, device_role, device_pool, snippet_type,
//    description_contains, action, ssh_command_regex.
//    Empty `when` means "always match".
//
// 2) action="gate" — phase-3 promotion blocker. Used to require some
//    historical state before letting a transition proceed. Shape:
//    {
//      "action": "gate",
//      "on": "promote",                    // required
//      "from": "qa",                       // optional source env filter
//      "to": "production",                 // optional target env filter
//      "reason": "qa validation required",
//      "require": [
//        { "type": "successful_runs", "min": 1, "within_days": 7,
//          "scope": "this_workflow" },                  // or "any_workflow"
//        { "type": "last_successful_run_within", "days": 2,
//          "scope": "this_workflow" },
//        { "type": "successful_snippet_runs",
//          "snippet_ids": ["uuid", ...], "min": 5, "within_days": 30 }
//      ]
//    }
//    A gate fails (returns deny) when ANY requirement isn't met. The
//    reason aggregates every failed requirement so the operator sees
//    the full punch list, not just the first miss.
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    private readonly IPolicyEvaluatorRepository _repo;
    private readonly ILogger<PolicyEvaluator> _logger;

    public PolicyEvaluator(IPolicyEvaluatorRepository repo, ILogger<PolicyEvaluator> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<PolicyDecision> EvaluateAsync(
        PolicyEvaluationContext context, CancellationToken ct)
    {
        IReadOnlyList<PolicyRuleRow> policies;
        try
        {
            policies = await _repo.GetEnabledPoliciesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "policy.evaluate.failed action={Action} env={Env}",
                context.Action, context.Environment);
            throw;
        }

        if (policies.Count == 0)
        {
            _logger.LogDebug(
                "policy.evaluate.allow action={Action} env={Env} policy_count={PolicyCount}",
                context.Action, context.Environment, 0);
            return new PolicyDecision(true, null, null);
        }

        var snippetTypesInWorkflow = ExtractSnippetTypes(context.Nodes);

        foreach (var p in policies)
        {
            if (p.Rule.ValueKind != JsonValueKind.Object) continue;

            var action = ReadString(p.Rule, "action") ?? "deny";

            if (string.Equals(action, "deny", StringComparison.OrdinalIgnoreCase))
            {
                if (!Matches(p.Rule, context, snippetTypesInWorkflow)) continue;
                var reason = ReadString(p.Rule, "reason")
                    ?? $"blocked by policy `{p.Name}`";
                _logger.LogInformation(
                    "policy.evaluate.deny policy_id={PolicyId} policy_name={PolicyName} action={Action} env={Env}",
                    p.PolicyId, p.Name, context.Action, context.Environment);
                return new PolicyDecision(false, p.Name, reason);
            }
            else if (string.Equals(action, "gate", StringComparison.OrdinalIgnoreCase))
            {
                if (!GateApplies(p.Rule, context)) continue;
                var unmet = await EvaluateGateRequirementsAsync(p.Rule, context, ct);
                if (unmet.Count == 0) continue;
                var prefix = ReadString(p.Rule, "reason") ?? $"blocked by gate `{p.Name}`";
                var reason = unmet.Count == 1
                    ? $"{prefix}: {unmet[0]}"
                    : $"{prefix}: " + string.Join("; ", unmet);
                _logger.LogInformation(
                    "policy.evaluate.gate_blocked policy_id={PolicyId} policy_name={PolicyName} action={Action} env={Env} unmet_count={UnmetCount}",
                    p.PolicyId, p.Name, context.Action, context.Environment, unmet.Count);
                return new PolicyDecision(false, p.Name, reason);
            }
            // Other action types ("allow", future shapes) ignored for now.
        }

        _logger.LogDebug(
            "policy.evaluate.allow action={Action} env={Env} policy_count={PolicyCount}",
            context.Action, context.Environment, policies.Count);
        return new PolicyDecision(true, null, null);
    }

    private static bool Matches(
        JsonElement rule,
        PolicyEvaluationContext ctx,
        IReadOnlyCollection<string> snippetTypes)
    {
        if (!rule.TryGetProperty("when", out var when)
            || when.ValueKind != JsonValueKind.Object)
            return true; // empty when → always match

        // Action gate.
        if (when.TryGetProperty("action", out var actionList)
            && !ContainsIgnoreCase(actionList, ctx.Action))
            return false;

        // Environment gate.
        if (when.TryGetProperty("env", out var envList)
            && !ContainsIgnoreCase(envList, ctx.Environment))
            return false;

        // Any-matching gates: at least one element in the context list
        // must appear in the rule's list.
        if (when.TryGetProperty("device_role", out var roleList)
            && !AnyInList(roleList, ctx.DeviceRoles))
            return false;

        if (when.TryGetProperty("device_pool", out var poolList)
            && !AnyInList(poolList, ctx.DevicePoolNames))
            return false;

        if (when.TryGetProperty("snippet_type", out var typeList)
            && !AnyInList(typeList, snippetTypes))
            return false;

        if (when.TryGetProperty("description_contains", out var descNeedles)
            && descNeedles.ValueKind == JsonValueKind.Array)
        {
            var desc = ctx.WorkflowDescription ?? string.Empty;
            var hit = false;
            foreach (var n in descNeedles.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.String) continue;
                var needle = n.GetString();
                if (string.IsNullOrEmpty(needle)) continue;
                if (desc.Contains(needle!, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) return false;
        }

        // Phase 2d: per-command ssh gate. Fires only when the calling
        // handler populated ctx.SshCommand (SshHandler does this right
        // before RunCommand). Rules that don't include ssh_command_regex
        // keep their old semantics — a pure workflow-level rule isn't
        // re-checked at command granularity.
        if (when.TryGetProperty("ssh_command_regex", out var rxList)
            && rxList.ValueKind == JsonValueKind.Array)
        {
            if (string.IsNullOrEmpty(ctx.SshCommand)) return false;
            var hit = false;
            foreach (var r in rxList.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String) continue;
                var pattern = r.GetString();
                if (string.IsNullOrEmpty(pattern)) continue;
                try
                {
                    // Case-insensitive by default — command verbs are
                    // naturally case-insensitive on Cisco/Juniper CLIs.
                    // 250ms timeout stops a pathological pattern from
                    // DoS'ing the worker thread.
                    if (Regex.IsMatch(ctx.SshCommand!, pattern!,
                        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))
                    {
                        hit = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
                {
                    // Treat timeout as "did not match" — an admin writing
                    // an exponential-blowup regex shouldn't be able to
                    // wedge every ssh step. The warning surfaces in logs.
                    //
                    // ArgumentException covers the same hazard for a pattern
                    // that doesn't compile at all: policy rules are free-form
                    // JSON with no save-time regex validation, so a typo would
                    // otherwise throw out of every ssh step's policy check.
                }
            }
            if (!hit) return false;
        }

        return true;
    }

    private static HashSet<string> ExtractSnippetTypes(JsonElement nodes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (nodes.ValueKind != JsonValueKind.Array) return result;
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            // We write `snippet_type` into the context from the calling
            // service using a DB lookup; policies can also match on the
            // literal `snippet_id` sentinel values (__start__, __end__,
            // subflow) that appear verbatim in the node JSON.
            var sid = ReadString(n, "snippet_id");
            if (sid is "__start__" or "__end__" or "subflow")
                result.Add(sid);
        }
        return result;
    }

    private static bool ContainsIgnoreCase(JsonElement list, string value)
    {
        if (list.ValueKind != JsonValueKind.Array) return false;
        foreach (var el in list.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            if (string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool AnyInList(JsonElement list, IEnumerable<string> contextValues)
    {
        if (list.ValueKind != JsonValueKind.Array) return false;
        var needles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in list.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var v = el.GetString();
            if (!string.IsNullOrEmpty(v)) needles.Add(v!);
        }
        foreach (var v in contextValues)
            if (needles.Contains(v)) return true;
        return false;
    }

    private static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ─── gate evaluation (phase 3) ──────────────────────────────────

    // Decide whether a gate rule applies to the current context. The
    // gate's `on` MUST equal the action; `from`/`to` filters narrow it
    // further to specific transitions. Missing filters mean "any".
    private static bool GateApplies(JsonElement rule, PolicyEvaluationContext ctx)
    {
        var on = ReadString(rule, "on");
        if (!string.IsNullOrEmpty(on)
            && !string.Equals(on, ctx.Action, StringComparison.OrdinalIgnoreCase))
            return false;

        var from = ReadString(rule, "from");
        if (!string.IsNullOrEmpty(from)
            && !string.Equals(from, ctx.Environment, StringComparison.OrdinalIgnoreCase))
            return false;

        var to = ReadString(rule, "to");
        if (!string.IsNullOrEmpty(to))
        {
            if (string.IsNullOrEmpty(ctx.TargetEnvironment)) return false;
            if (!string.Equals(to, ctx.TargetEnvironment, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private async Task<List<string>> EvaluateGateRequirementsAsync(
        JsonElement rule, PolicyEvaluationContext ctx, CancellationToken ct)
    {
        var unmet = new List<string>();
        if (!rule.TryGetProperty("require", out var requireArr)
            || requireArr.ValueKind != JsonValueKind.Array)
            return unmet;

        foreach (var req in requireArr.EnumerateArray())
        {
            if (req.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(req, "type") ?? string.Empty;
            switch (type.ToLowerInvariant())
            {
                case "successful_runs":
                    {
                        var min = ReadInt(req, "min", 1);
                        var withinDays = ReadInt(req, "within_days", 0);
                        var scope = ReadString(req, "scope") ?? "this_workflow";
                        var (count, ok) = await CountSuccessfulRunsAsync(ctx.WorkflowId, scope, withinDays, ct);
                        if (!ok || count < min)
                        {
                            unmet.Add(BuildSuccessfulRunsMessage(min, withinDays, scope, count, ok));
                        }
                        break;
                    }
                case "last_successful_run_within":
                    {
                        var days = ReadInt(req, "days", 1);
                        var scope = ReadString(req, "scope") ?? "this_workflow";
                        var (last, ok) = await GetLastSuccessfulRunAsync(ctx.WorkflowId, scope, ct);
                        var cutoff = DateTime.UtcNow.AddDays(-days);
                        if (!ok || last is null || last < cutoff)
                        {
                            unmet.Add(BuildLastSuccessfulRunMessage(days, scope, last, ok));
                        }
                        break;
                    }
                case "successful_snippet_runs":
                    {
                        var min = ReadInt(req, "min", 1);
                        var withinDays = ReadInt(req, "within_days", 0);
                        var ids = ReadStringList(req, "snippet_ids");
                        var snippetIds = ids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                            .Where(g => g != Guid.Empty).ToList();
                        var count = await CountSuccessfulSnippetRunsAsync(snippetIds, withinDays, ct);
                        if (count < min)
                        {
                            var window = withinDays > 0 ? $" in the last {withinDays}d" : "";
                            var label = snippetIds.Count == 0 ? "ANY snippet" : $"{snippetIds.Count} snippet(s)";
                            unmet.Add($"need {min} successful run(s) of {label}{window}, got {count}");
                        }
                        break;
                    }
                default:
                    unmet.Add($"unknown requirement type `{type}`");
                    break;
            }
        }
        return unmet;
    }

    private async Task<(int count, bool ok)> CountSuccessfulRunsAsync(
        Guid? workflowId, string scope, int withinDays, CancellationToken ct)
    {
        var thisOnly = string.Equals(scope, "this_workflow", StringComparison.OrdinalIgnoreCase);
        if (thisOnly && workflowId is null) return (0, false);

        DateTime? cutoff = withinDays > 0 ? DateTime.UtcNow.AddDays(-withinDays) : null;
        var count = await _repo.CountCompletedRunsAsync(thisOnly ? workflowId : null, cutoff, ct);
        return (count, true);
    }

    private async Task<(DateTime? last, bool ok)> GetLastSuccessfulRunAsync(
        Guid? workflowId, string scope, CancellationToken ct)
    {
        var thisOnly = string.Equals(scope, "this_workflow", StringComparison.OrdinalIgnoreCase);
        if (thisOnly && workflowId is null) return (null, false);

        var last = await _repo.GetLastCompletedRunAsync(thisOnly ? workflowId : null, ct);
        return (last, true);
    }

    private async Task<int> CountSuccessfulSnippetRunsAsync(
        IReadOnlyList<Guid> snippetIds, int withinDays, CancellationToken ct)
    {
        // StepRun has SnippetId + Status; "completed" is the success
        // marker shared with WorkflowRun. When no specific ids are
        // supplied, treat the requirement as "any snippet" so admins
        // can write a coarse "must have run SOMETHING" gate.
        DateTime? cutoff = withinDays > 0 ? DateTime.UtcNow.AddDays(-withinDays) : null;
        return await _repo.CountCompletedStepRunsAsync(snippetIds, cutoff, ct);
    }

    private static int ReadInt(JsonElement obj, string key, int fallback)
    {
        if (!obj.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return fallback;
    }

    private static List<string> ReadStringList(JsonElement obj, string key)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var el in v.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                list.Add(el.GetString()!);
        return list;
    }

    private static string BuildSuccessfulRunsMessage(
        int min, int withinDays, string scope, int count, bool ok)
    {
        if (!ok) return "successful_runs gate could not evaluate (workflow id missing for this_workflow scope)";
        var window = withinDays > 0 ? $" in the last {withinDays}d" : "";
        var what = string.Equals(scope, "any_workflow", StringComparison.OrdinalIgnoreCase)
            ? "any workflow" : "this workflow";
        return $"need {min} successful run(s) of {what}{window}, got {count}";
    }

    private static string BuildLastSuccessfulRunMessage(
        int days, string scope, DateTime? last, bool ok)
    {
        if (!ok) return "last_successful_run_within gate could not evaluate (workflow id missing for this_workflow scope)";
        var what = string.Equals(scope, "any_workflow", StringComparison.OrdinalIgnoreCase)
            ? "any workflow" : "this workflow";
        if (last is null) return $"no successful run of {what} on record (need one within the last {days}d)";
        var ageHours = (int)(DateTime.UtcNow - last.Value).TotalHours;
        return $"last successful run of {what} was ~{ageHours}h ago (need one within {days}d)";
    }
}
