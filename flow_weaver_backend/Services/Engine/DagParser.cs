using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace flow_weaver_backend.Services.Engine;

// Turns the raw (nodes, edges) jsonb stored on a Workflow row into a
// validated in-memory graph. The JSON Schema validator (Sprint 1.6)
// already guarantees shape — this parser adds the structural invariants
// JSON Schema cannot express:
//
//   - node ids are unique across the array
//   - every edge references existing source/target nodes
//   - the graph is acyclic (DFS with gray/black coloring)
//   - at least one node has in-degree 0 (entry point exists)
//   - conditional edges carry a non-empty condition string
//
// On any violation: DagParseException, which the executor surfaces as a
// `workflow_run.status = "failed"` with the message in step_runs.Error.
public class DagParser
{
    private readonly ILogger<DagParser> _logger;

    public DagParser(ILogger<DagParser> logger)
    {
        _logger = logger;
    }

    public Dag Parse(JsonElement nodesJson, JsonElement edgesJson)
    {
        try
        {
            if (nodesJson.ValueKind != JsonValueKind.Array)
                throw new DagParseException("`nodes` must be a JSON array");
            if (edgesJson.ValueKind != JsonValueKind.Array)
                throw new DagParseException("`edges` must be a JSON array");

            var nodes = ParseNodes(nodesJson);
            var edges = ParseEdges(edgesJson, nodes);

            var adjacency = BuildAdjacency(nodes, edges);
            var inDegree = BuildInDegree(nodes, edges);

            if (!nodes.Values.Any(n => inDegree[n.Id] == 0))
                throw new DagParseException("no start node — every node has an incoming edge");

            DetectCycles(nodes.Keys, adjacency);

            _logger.LogDebug(
                "engine.dag.parse.ok nodes={NodeCount} edges={EdgeCount}",
                nodes.Count, edges.Count);

            return new Dag(nodes, edges, adjacency, inDegree);
        }
        catch (DagParseException ex)
        {
            _logger.LogWarning("engine.dag.parse.failed reason={Reason}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "engine.dag.parse.failed reason=unexpected_error");
            throw;
        }
    }

    private static Dictionary<string, WorkflowNode> ParseNodes(JsonElement nodesJson)
    {
        var result = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var el in nodesJson.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new DagParseException("node is missing a string `id`");
            var id = idEl.GetString()!;
            if (string.IsNullOrWhiteSpace(id))
                throw new DagParseException("node id cannot be empty");
            if (result.ContainsKey(id))
                throw new DagParseException($"duplicate node id `{id}`");

            if (!el.TryGetProperty("snippet_id", out var svcEl) || svcEl.ValueKind != JsonValueKind.String)
                throw new DagParseException($"node `{id}` is missing a string `snippet_id`");
            var snippetId = svcEl.GetString()!;

            var nodeType = "task";
            if (el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                nodeType = typeEl.GetString()!;
                if (nodeType is not ("task" or "decision" or "subflow"))
                    throw new DagParseException($"node `{id}` has invalid type `{nodeType}` (expected task/decision/subflow)");
            }

            JsonElement cfg = default;
            if (el.TryGetProperty("config_overrides", out var cfgEl))
                cfg = cfgEl;

            result[id] = new WorkflowNode(id, snippetId, nodeType, cfg);
        }
        return result;
    }

    private static List<WorkflowEdge> ParseEdges(
        JsonElement edgesJson, IReadOnlyDictionary<string, WorkflowNode> nodes)
    {
        var result = new List<WorkflowEdge>();
        foreach (var el in edgesJson.EnumerateArray())
        {
            var source = el.GetProperty("source").GetString()
                ?? throw new DagParseException("edge is missing `source`");
            var target = el.GetProperty("target").GetString()
                ?? throw new DagParseException("edge is missing `target`");
            var type = el.GetProperty("type").GetString()
                ?? throw new DagParseException("edge is missing `type`");

            if (!nodes.ContainsKey(source))
                throw new DagParseException($"edge source `{source}` does not match any node id");
            if (!nodes.ContainsKey(target))
                throw new DagParseException($"edge target `{target}` does not match any node id");
            if (type is not ("success" or "failure" or "always" or "conditional"))
                throw new DagParseException($"edge `{source}->{target}` has invalid type `{type}`");

            string? condition = null;
            if (el.TryGetProperty("condition", out var condEl) && condEl.ValueKind == JsonValueKind.String)
                condition = condEl.GetString();
            if (type == "conditional" && string.IsNullOrWhiteSpace(condition))
                throw new DagParseException($"conditional edge `{source}->{target}` requires a non-empty condition");

            result.Add(new WorkflowEdge(source, target, type, condition));
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<WorkflowEdge>> BuildAdjacency(
        IReadOnlyDictionary<string, WorkflowNode> nodes, List<WorkflowEdge> edges)
    {
        var adj = nodes.Keys.ToDictionary(
            id => id,
            _ => (IReadOnlyList<WorkflowEdge>)new List<WorkflowEdge>());

        foreach (var e in edges)
            ((List<WorkflowEdge>)adj[e.Source]).Add(e);

        return adj;
    }

    private static Dictionary<string, int> BuildInDegree(
        IReadOnlyDictionary<string, WorkflowNode> nodes, List<WorkflowEdge> edges)
    {
        var inDegree = nodes.Keys.ToDictionary(id => id, _ => 0);
        foreach (var e in edges) inDegree[e.Target] += 1;
        return inDegree;
    }

    private static void DetectCycles(
        IEnumerable<string> nodeIds,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEdge>> adjacency)
    {
        // White = unvisited, Gray = in current DFS stack, Black = done.
        // Seeing a Gray node again during DFS is a back edge → cycle.
        const int White = 0, Gray = 1, Black = 2;
        var color = nodeIds.ToDictionary(id => id, _ => White);

        foreach (var id in color.Keys.ToList())
        {
            if (color[id] == White)
                Visit(id, color, adjacency, Gray, Black);
        }
    }

    private static void Visit(
        string id,
        Dictionary<string, int> color,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEdge>> adjacency,
        int gray, int black)
    {
        color[id] = gray;
        if (adjacency.TryGetValue(id, out var outgoing))
        {
            foreach (var edge in outgoing)
            {
                var next = edge.Target;
                if (color[next] == gray)
                    throw new DagParseException($"cycle detected at node `{next}`");
                if (color[next] == 0)
                    Visit(next, color, adjacency, gray, black);
            }
        }
        color[id] = black;
    }
    // Sprint 3.3: detect cycles across subflow references. Called by
    // the executor at EnqueueRunAsync to ensure a workflow that includes
    // subflows doesn't transitively reference itself.
    //
    // `resolveNodes` is a callback that loads the Nodes jsonb for a given
    // workflow id (lets the caller restrict the lookup to active rows).
    public static async Task DetectSubflowCyclesAsync(
        Guid rootWorkflowId,
        JsonElement rootNodes,
        Func<Guid, Task<JsonElement?>> resolveNodes)
    {
        var visited = new HashSet<Guid>();
        var stack = new HashSet<Guid>();
        await VisitSubflows(rootWorkflowId, rootNodes, resolveNodes, visited, stack);
    }

    private static async Task VisitSubflows(
        Guid workflowId,
        JsonElement nodes,
        Func<Guid, Task<JsonElement?>> resolveNodes,
        HashSet<Guid> visited,
        HashSet<Guid> stack)
    {
        if (!stack.Add(workflowId))
            throw new DagParseException($"subflow cycle detected — workflow {workflowId} references itself transitively");
        if (!visited.Add(workflowId)) { stack.Remove(workflowId); return; }

        if (nodes.ValueKind != JsonValueKind.Array) { stack.Remove(workflowId); return; }

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("snippet_id", out var sdi)
                || sdi.GetString() != "subflow") continue;
            if (!node.TryGetProperty("config_overrides", out var co)
                || !co.TryGetProperty("subflow_workflow_id", out var sfId)
                || !Guid.TryParse(sfId.GetString(), out var childWfId)) continue;

            var childNodes = await resolveNodes(childWfId);
            if (childNodes is null)
                throw new DagParseException($"subflow references non-existent workflow {childWfId}");

            await VisitSubflows(childWfId, childNodes.Value, resolveNodes, visited, stack);
        }

        stack.Remove(workflowId);
    }
}

public sealed class DagParseException : Exception
{
    public DagParseException(string message) : base(message) { }
}
