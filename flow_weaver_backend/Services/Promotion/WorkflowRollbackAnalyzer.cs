using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Worker;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Promotion;

public sealed record RollbackRisk(
    Guid SnippetId,
    string SnippetName,
    string SnippetType,
    IdempotencyKind EffectiveKind,
    bool HasFailureEdge);

public sealed record WorkflowRollbackReport(
    IReadOnlyList<RollbackRisk> NonReversible,
    IReadOnlyList<RollbackRisk> RequiresCompensation,
    IReadOnlyList<RollbackRisk> CompensatingFailureEdges);

// Walks a Workflow's node graph, resolves the IdempotencyKind for each
// referenced snippet, and reports which nodes block a clean rollback.
//
// "Clean rollback" rules:
//   • Idempotent nodes always pass.
//   • RequiresCompensation nodes need a `failure` edge from them to a
//     downstream node that performs the compensation. The analyzer
//     does not verify the semantics of the failure edge — only that it
//     exists. Authors are responsible for putting the actual revert
//     action there.
//   • NonReversible nodes block rollback unconditionally.
public sealed class WorkflowRollbackAnalyzer
{
    private readonly IRepository<SnippetModel> _snippets;
    private readonly IEnumerable<ISnippetHandler> _handlers;

    public WorkflowRollbackAnalyzer(IRepository<SnippetModel> snippets, IEnumerable<ISnippetHandler> handlers)
    {
        _snippets = snippets;
        _handlers = handlers;
    }

    public async Task<WorkflowRollbackReport> AnalyzeAsync(
        WorkflowModel workflow, CancellationToken ct)
    {
        var (nodeSnippetIds, nodesWithFailureEdge, nodeIdempotency) = ParseGraph(workflow);
        if (nodeSnippetIds.Count == 0)
            return new WorkflowRollbackReport(
                Array.Empty<RollbackRisk>(),
                Array.Empty<RollbackRisk>(),
                Array.Empty<RollbackRisk>());

        var snippetIds = nodeSnippetIds.Values
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        // activeOnly:false mirrors the original query — a historical snapshot
        // may reference snippets that have since been soft-deleted, and we
        // still need their idempotency.
        var snippets = await _snippets.ListByIdsAsync(snippetIds, activeOnly: false, ct: ct);
        var byId = snippets.ToDictionary(s => s.SnippetId);

        var handlerKindByType = _handlers
            .GroupBy(h => h.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DefaultIdempotency, StringComparer.OrdinalIgnoreCase);

        var nonReversible = new List<RollbackRisk>();
        var requiresComp = new List<RollbackRisk>();
        var compensated = new List<RollbackRisk>();

        foreach (var (nodeId, snippetId) in nodeSnippetIds)
        {
            if (!byId.TryGetValue(snippetId, out var snippet)) continue;

            var handlerKind = handlerKindByType.TryGetValue(snippet.Type, out var hk)
                ? hk
                : IdempotencyKind.RequiresCompensation;

            nodeIdempotency.TryGetValue(nodeId, out var declared);
            var effective = EffectiveKind(handlerKind, snippet.Idempotency, declared);

            if (effective == IdempotencyKind.Idempotent) continue;

            var hasFailureEdge = nodesWithFailureEdge.Contains(nodeId);
            var risk = new RollbackRisk(snippet.SnippetId, snippet.Name, snippet.Type, effective, hasFailureEdge);

            if (effective == IdempotencyKind.NonReversible)
                nonReversible.Add(risk);
            else if (hasFailureEdge)
                compensated.Add(risk);
            else
                requiresComp.Add(risk);
        }

        return new WorkflowRollbackReport(nonReversible, requiresComp, compensated);
    }

    // ─── Graph parsing ──────────────────────────────────────────────────
    //
    // Workflow nodes / edges are stored as jsonb. We accept both the
    // flat shape used by the editor (nodes: [{ id, snippet_id }],
    // edges: [{ source, target, type }]) and the nested form some
    // exporters produce. Anything we can't recognise contributes
    // nothing to the analysis — better to under-report than to fail
    // promotion on a parse miss.
    private static (Dictionary<string, Guid> NodeSnippetIds,
                    HashSet<string> NodesWithFailureEdge,
                    Dictionary<string, string> NodeIdempotency) ParseGraph(WorkflowModel workflow)
    {
        var nodeSnippetIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var failureEdgeSources = new HashSet<string>(StringComparer.Ordinal);
        var nodeIdempotency = new Dictionary<string, string>(StringComparer.Ordinal);

        if (workflow.Nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in workflow.Nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                var id = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(id)) continue;

                Guid snippetId = Guid.Empty;
                if (node.TryGetProperty("snippet_id", out var snEl) && snEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(snEl.GetString(), out var sg))
                    snippetId = sg;
                else if (node.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
                    && dataEl.TryGetProperty("snippet_id", out var dSnEl) && dSnEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(dSnEl.GetString(), out var dsg))
                    snippetId = dsg;

                if (snippetId != Guid.Empty)
                    nodeSnippetIds[id!] = snippetId;

                if (node.TryGetProperty("config_overrides", out var overrides)
                    && overrides.ValueKind == JsonValueKind.Object
                    && overrides.TryGetProperty("idempotency", out var idem)
                    && idem.ValueKind == JsonValueKind.String
                    && idem.GetString() is { Length: > 0 } tier)
                    nodeIdempotency[id!] = tier;
            }
        }

        if (workflow.Edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in workflow.Edges.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object) continue;
                var source = edge.TryGetProperty("source", out var sEl) ? sEl.GetString() : null;
                var type = edge.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(type)) continue;
                if (string.Equals(type, "failure", StringComparison.OrdinalIgnoreCase))
                    failureEdgeSources.Add(source!);
            }
        }

        return (nodeSnippetIds, failureEdgeSources, nodeIdempotency);
    }

    /// <summary>
    /// The tier that actually applies to one node. The ONE place it is decided.
    /// </summary>
    /// <remarks>
    /// Extracted from this analyzer's own loop so the executor can score a finished run the
    /// same way promotion scores a graph. The reference engine's note on the equivalent function is
    /// the reason it is one function and not two: the two used to disagree there, and a run
    /// reported `rolled_back` for an email it could not unsend.
    ///
    /// Two rules, in order:
    ///   - A NonReversible HANDLER is absolute. An author must not be able to declare a
    ///     compensation for sending an email or pushing a commit, because there is none.
    ///   - Below that ceiling the snippet's own tier applies, and the node's
    ///     `config_overrides.idempotency` may RAISE it and never lower it
    ///     (execution/SPEC.md §2) — a node may not talk its shared snippet out of being
    ///     non-reversible.
    ///
    /// An unrecognised word is not a declaration: it leaves the tier alone rather than
    /// silently raising it on a typo.
    /// </remarks>
    public static IdempotencyKind EffectiveKind(
        IdempotencyKind handlerKind, string? snippetIdempotency, string? nodeIdempotency)
    {
        var effective = handlerKind == IdempotencyKind.NonReversible
            ? IdempotencyKind.NonReversible
            : (ParseKind(snippetIdempotency) ?? handlerKind);

        if (ParseKind(nodeIdempotency) is { } nodeTier && nodeTier > effective)
            effective = nodeTier;

        return effective;
    }

    /// <summary>
    /// A tier's wire value, or null when the string names no tier. Public because a recorded
    /// tier — a `subflow` step's, read back from the child run — has to be parsed by the same
    /// reader that parses a declared one.
    /// </summary>
    public static IdempotencyKind? ParseTier(string? raw) => ParseKind(raw);

    private static IdempotencyKind? ParseKind(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "idempotent" => IdempotencyKind.Idempotent,
        "requires_compensation" => IdempotencyKind.RequiresCompensation,
        "non_reversible" => IdempotencyKind.NonReversible,
        _ => null,
    };
}
