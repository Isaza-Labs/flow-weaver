using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Dry-run a workflow without touching any device or external
// system. The agent (or the admin, via chat) can use this to catch
// missing snippets, unresolvable templates, orphan nodes, and broken
// integration references BEFORE issuing run_workflow against real
// targets. Think of it as `terraform plan` for a DAG.
//
// What we check today (everything is read-only, no handler calls):
//   1. Workflow exists.
//   2. Every node's snippet_id resolves: valid sentinel, OR a real
//      Snippet row, OR a subflow placeholder.
//   3. For integration_action nodes, the referenced integration +
//      action exist and the integration's last health status is
//      reported — a lab that's down doesn't hard-fail the simulation
//      but shows up in the warnings list so the operator notices.
//   4. Edges reference node ids that actually exist; success/failure
//      types are legal; conditional edges declare a condition string.
//   5. Templated strings in config_overrides reference steps that are
//      upstream of the node using them (ordering check).
//
// Not covered yet (documented in the return payload): live device
// resolution, target_mode fan-out sizing, per-device auth checks.
public sealed class SimulateWorkflowRunHandler : IToolHandler
{
    public string Name => "simulate_workflow_run";

    public string Description =>
        "Tier: autonomous. Statically validate a workflow without executing " +
        "any handler. Returns structural issues (missing snippets, unknown " +
        "integrations, broken edges, upstream/downstream template mismatches) " +
        "so the operator can fix them before calling run_workflow. Fire " +
        "freely — this is a read-only dry-run, no confirmation needed.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["workflow_id"],
          "properties": {
            "workflow_id": { "type": "string", "format": "uuid" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ISnippetRepository _snippets;
    private readonly IIntegrationRepository _integrations;
    private readonly IIntegrationActionRepository _actions;
    private readonly ISimulationResultRepository _simulations;
    private readonly ICurrentUser _caller;
    private readonly ILogger<SimulateWorkflowRunHandler> _logger;

    public SimulateWorkflowRunHandler(
        IRepository<WorkflowModel> workflows,
        ISnippetRepository snippets,
        IIntegrationRepository integrations,
        IIntegrationActionRepository actions,
        ISimulationResultRepository simulations,
        ICurrentUser caller,
        ILogger<SimulateWorkflowRunHandler> logger)
    {
        _workflows = workflows;
        _snippets = snippets;
        _integrations = integrations;
        _actions = actions;
        _simulations = simulations;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("workflow_id", out var widEl)
            || widEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(widEl.GetString(), out var workflowId))
        {
            _logger.LogWarning("ai.tool.simulate_workflow_run.validation_failed reason=workflow_id_required");
            return JsonSerializer.SerializeToElement(new { error = "workflow_id (uuid) is required" });
        }

        _logger.LogDebug("ai.tool.simulate_workflow_run.start workflow_id={WorkflowId}", workflowId);

        // Tracked here (not AsNoTracking) so we can update LastSimulationId
        // on the same row at the end of the dispatch.
        var wf = await _workflows.GetByIdAsync(workflowId, activeOnly: true, tracking: true, ct);
        if (wf is null)
        {
            _logger.LogWarning("ai.tool.simulate_workflow_run.not_found workflow_id={WorkflowId}", workflowId);
            return JsonSerializer.SerializeToElement(new { error = "workflow not found" });
        }

        // Simulation is only meaningful for drafts — promoted rows are
        // immutable (WorkflowService blocks edits to qa/production) and
        // their gate already ran when they got promoted. Stamping
        // LastSimulationId on a non-draft would be wasted work.
        if (!string.Equals(wf.Environment, "draft", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ai.tool.simulate_workflow_run.rejected workflow_id={WorkflowId} environment={Environment}",
                workflowId, wf.Environment);
            return JsonSerializer.SerializeToElement(new
            {
                error = $"simulate_workflow_run only runs on drafts (current environment: {wf.Environment}). " +
                        "To re-validate a promoted workflow, clone it to a draft first.",
            });
        }

        var issues = new List<object>();
        var warnings = new List<object>();

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeSnippetIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeConfigs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var nodeIntegrationRefs = new Dictionary<string, (Guid IntegrationId, Guid ActionId)>(StringComparer.Ordinal);

        if (wf.Nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in wf.Nodes.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(n, "id") ?? string.Empty;
                var snip = ReadString(n, "snippet_id") ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;
                if (!nodeIds.Add(id))
                    issues.Add(new { kind = "duplicate_node_id", node_id = id });
                nodeSnippetIds[id] = snip;
                if (n.TryGetProperty("config_overrides", out var co)) nodeConfigs[id] = co;

                if (string.Equals(snip, "integration_action", StringComparison.Ordinal))
                {
                    // Legacy sentinel shape — before FR snippet rename,
                    // this was the string literal. Flag it: executor
                    // rejects, and we want the simulator to tell the
                    // user before they click Run.
                    issues.Add(new
                    {
                        kind = "literal_integration_action",
                        node_id = id,
                        hint = "snippet_id=\"integration_action\" is invalid. Reference the UUID of the seeded snippet with type=integration_action via fw_snippets:list_snippets.",
                    });
                }

                // Pull integration_id + action_id for later existence check.
                if (n.TryGetProperty("config_overrides", out var cfg)
                    && cfg.ValueKind == JsonValueKind.Object)
                {
                    var integrationId = TryGetGuid(cfg, "integration_id");
                    var actionId = TryGetGuid(cfg, "action_id");
                    if (integrationId.HasValue && actionId.HasValue)
                        nodeIntegrationRefs[id] = (integrationId.Value, actionId.Value);
                }
            }
        }

        // Snippet id existence check. Sentinels (start/end/subflow) and
        // `integration_action` literal don't count — we flag the literal
        // separately above. Everything else should be a Guid that maps
        // to an active Snippet row.
        var realSnippetIds = nodeSnippetIds.Values
            .Where(s => Guid.TryParse(s, out _))
            .Select(Guid.Parse)
            .Distinct()
            .ToList();
        var existingSnippetIds = await _snippets.ExistingActiveIdsAsync(realSnippetIds, ct);
        foreach (var (nodeId, snipStr) in nodeSnippetIds)
        {
            if (snipStr is "__start__" or "__end__" or "subflow") continue;
            if (snipStr == "integration_action") continue; // flagged above
            if (!Guid.TryParse(snipStr, out var sid))
            {
                issues.Add(new { kind = "invalid_snippet_id", node_id = nodeId, snippet_id = snipStr });
                continue;
            }
            if (!existingSnippetIds.Contains(sid))
                issues.Add(new { kind = "snippet_not_found", node_id = nodeId, snippet_id = sid });
        }

        // Integration + action existence. Also report last health.
        if (nodeIntegrationRefs.Count > 0)
        {
            var wantedIntegrations = nodeIntegrationRefs.Values.Select(v => v.IntegrationId).Distinct().ToList();
            var wantedActions = nodeIntegrationRefs.Values.Select(v => v.ActionId).Distinct().ToList();
            var integrations = await _integrations.ListActiveRefsByIdsAsync(wantedIntegrations, ct);
            var actions = await _actions.ListActiveRefsByIdsAsync(wantedActions, ct);
            var iIndex = integrations.ToDictionary(i => i.IntegrationId);
            var aIndex = actions.ToDictionary(a => a.IntegrationActionId);

            foreach (var (nodeId, refs) in nodeIntegrationRefs)
            {
                if (!iIndex.ContainsKey(refs.IntegrationId))
                    issues.Add(new { kind = "integration_not_found", node_id = nodeId, integration_id = refs.IntegrationId });
                else if (!string.Equals(iIndex[refs.IntegrationId].Status, "healthy", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(iIndex[refs.IntegrationId].Status))
                    warnings.Add(new
                    {
                        kind = "integration_unhealthy",
                        node_id = nodeId,
                        integration = iIndex[refs.IntegrationId].Name,
                        status = iIndex[refs.IntegrationId].Status,
                    });
                if (!aIndex.ContainsKey(refs.ActionId))
                    issues.Add(new { kind = "integration_action_not_found", node_id = nodeId, action_id = refs.ActionId });
            }
        }

        // Edge validation.
        if (wf.Edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in wf.Edges.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var src = ReadString(e, "source") ?? string.Empty;
                var tgt = ReadString(e, "target") ?? string.Empty;
                var type = ReadString(e, "type") ?? string.Empty;
                if (!nodeIds.Contains(src)) issues.Add(new { kind = "edge_source_missing", source = src, target = tgt });
                if (!nodeIds.Contains(tgt)) issues.Add(new { kind = "edge_target_missing", source = src, target = tgt });
                if (type is not ("success" or "failure" or "always" or "conditional"))
                    issues.Add(new { kind = "invalid_edge_type", source = src, target = tgt, type });
                if (type == "conditional"
                    && string.IsNullOrWhiteSpace(ReadString(e, "condition")))
                    issues.Add(new { kind = "conditional_edge_missing_condition", source = src, target = tgt });
            }
        }

        // Template reference ordering. For each node's config_overrides,
        // find `{{ steps.X.output.* }}` refs and ensure X is upstream.
        var upstream = BuildUpstreamMap(wf.Edges, nodeIds);
        foreach (var (nodeId, cfg) in nodeConfigs)
        {
            var refs = ExtractStepRefs(cfg);
            foreach (var refNode in refs)
            {
                if (!nodeIds.Contains(refNode))
                {
                    issues.Add(new { kind = "template_refers_missing_node", node_id = nodeId, refers_to = refNode });
                    continue;
                }
                if (!upstream.TryGetValue(nodeId, out var set) || !set.Contains(refNode))
                    issues.Add(new { kind = "template_refers_non_upstream", node_id = nodeId, refers_to = refNode });
            }
        }

        var ok = issues.Count == 0;
        var now = DateTime.UtcNow;
        var schemaHash = ComputeSchemaHash(wf.Nodes, wf.Edges);

        // Persist the result so PromotionService.draft→qa and
        // mark_workflow_ready can verify a fresh successful simulation
        // exists for THIS graph (the SchemaHash filter rules out stale
        // rows that pre-date a subsequent edit).
        var simulation = new SimulationResult
        {
            SimulationResultId = Guid.NewGuid(),
            WorkflowId = wf.WorkflowId,
            NodeCount = nodeIds.Count,
            IssueCount = issues.Count,
            WarningCount = warnings.Count,
            Ok = ok,
            SchemaHash = schemaHash,
            Issues = JsonSerializer.SerializeToElement(issues),
            Warnings = JsonSerializer.SerializeToElement(warnings),
            SimulatedAt = now,
            SimulatedBy = _caller.Username ?? string.Empty,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _simulations.Add(simulation);

        // Promote this result to the workflow's "current truth". Cleared
        // on edits via WorkflowService.UpdateAsync (which we touch in
        // a companion edit).
        //
        // Concurrency note: there is a benign race where a concurrent
        // edit bumps Nodes/Edges between our FirstOrDefaultAsync and
        // this SaveChangesAsync. EF only writes the properties we
        // modified (LastSimulationId, UpdatedAt), so the editor's Nodes
        // are not lost. But our LastSimulationId then points at a
        // SimulationResult whose SchemaHash no longer matches the
        // workflow — exactly what the gate is designed to detect via
        // the hash comparison. Until we adopt RowVersion across the
        // model, the hash check is the safety net (see AGENTS.md §2
        // and the Sprint 16 follow-up tracker).
        wf.LastSimulationId = simulation.SimulationResultId;
        wf.UpdatedAt = now;

        try
        {
            // One SaveChanges flushes both the new SimulationResult and the
            // tracked workflow's LastSimulationId update — they share the
            // scoped AppDbContext.
            await _simulations.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Persistence failure shouldn't strand the agent — it still
            // gets the in-memory verdict and can retry. But we log loud
            // because the gate downstream will treat the workflow as
            // un-simulated.
            _logger.LogError(ex,
                "ai.tool.simulate_workflow_run.persist_failed workflow_id={WorkflowId} ok={Ok}",
                workflowId, ok);
        }

        _logger.LogInformation(
            "ai.tool.simulate_workflow_run.ok workflow_id={WorkflowId} node_count={NodeCount} issue_count={IssueCount} warning_count={WarningCount} simulation_ok={SimulationOk} schema_hash={SchemaHash}",
            workflowId, nodeIds.Count, issues.Count, warnings.Count, ok, schemaHash);

        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = wf.WorkflowId,
            environment = wf.Environment,
            version = wf.Version,
            simulation_id = simulation.SimulationResultId,
            schema_hash = schemaHash,
            node_count = nodeIds.Count,
            ok,
            issue_count = issues.Count,
            warning_count = warnings.Count,
            issues,
            warnings,
            notes = new[]
            {
                "Simulation does not validate live device reachability or target_mode fan-out — use run_workflow for that.",
                "Integration auth isn't exercised; an unhealthy status is surfaced as a warning, not a blocker.",
            },
        });
    }

    // Deterministic SHA-256 of the (Nodes, Edges) graph, encoded as
    // lowercase hex. The hash is taken over a CANONICAL serialization
    // (object keys sorted recursively) because Postgres jsonb does not
    // preserve insertion order — `wf.Nodes.GetRawText()` after a
    // round-trip can differ from the text that went in even though the
    // semantic content is identical. Without canonicalization, the
    // `simulation_stale` gate fires spuriously.
    // Moved to Services/Workflow/WorkflowCanonicalizer — it is the identity of a graph, and
    // promotion and the workflow gate both depend on it, so filing it under an assistant tool
    // put a shared contract behind a feature boundary. This member stays as a delegator
    // because three test files and two services already call it by this name; the parity
    // programme's file map will retire it.
    internal static string ComputeSchemaHash(JsonElement nodes, JsonElement edges) =>
        Services.Workflow.WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);
    private static Dictionary<string, HashSet<string>> BuildUpstreamMap(JsonElement edges, HashSet<string> nodeIds)
    {
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in nodeIds) direct[id] = new HashSet<string>(StringComparer.Ordinal);
        if (edges.ValueKind != JsonValueKind.Array) return direct;
        foreach (var e in edges.EnumerateArray())
        {
            var src = ReadString(e, "source");
            var tgt = ReadString(e, "target");
            if (src is null || tgt is null) continue;
            if (direct.TryGetValue(tgt, out var set)) set.Add(src);
        }
        // Transitively close — a node's upstream includes grandparents.
        var closed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in nodeIds)
        {
            var acc = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new Stack<string>();
            if (direct.TryGetValue(id, out var parents))
                foreach (var p in parents) frontier.Push(p);
            while (frontier.Count > 0)
            {
                var cur = frontier.Pop();
                if (!acc.Add(cur)) continue;
                if (direct.TryGetValue(cur, out var more))
                    foreach (var p in more) frontier.Push(p);
            }
            closed[id] = acc;
        }
        return closed;
    }

    private static readonly System.Text.RegularExpressions.Regex StepRefPattern =
        new(@"\{\{\s*steps\.([\w-]+)\.output", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static HashSet<string> ExtractStepRefs(JsonElement cfg)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Walk(cfg);
        return result;

        void Walk(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    foreach (System.Text.RegularExpressions.Match m in StepRefPattern.Matches(el.GetString() ?? string.Empty))
                        result.Add(m.Groups[1].Value);
                    break;
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var i in el.EnumerateArray()) Walk(i);
                    break;
            }
        }
    }

    // "1.00" → "1", "1.50" → "1.5", "1" → "1", "1.5" → "1.5". Only
    // operates on strings produced by Decimal.ToString — never strips
    // a leading or solo "0". The check for '.' avoids mangling integers.
    private static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static Guid? TryGetGuid(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var g)
                ? g
                : null;
}
