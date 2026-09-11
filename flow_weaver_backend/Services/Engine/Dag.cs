using System.Text.Json;

namespace flow_weaver_backend.Services.Engine;

// Parsed shape of one DAG node. Held in memory while the executor walks
// the workflow — never persisted directly (Nodes is a jsonb blob on the
// Workflow row).
//
// SnippetId is kept as a string because the schema lets it be either a
// Guid or one of the reserved sentinel values ("__start__", "__end__",
// "subflow"). Forcing a Guid parse here would lose the sentinel path.
public sealed record WorkflowNode(
    string Id,
    string SnippetId,
    string NodeType,
    JsonElement ConfigOverrides);

// Directed edge. EdgeType drives which branch the executor takes after a
// step finishes: success/failure propagate from the step's result,
// `always` fires regardless, and `conditional` is evaluated by the
// ConditionEvaluator (Sprint 2.7).
public sealed record WorkflowEdge(
    string Source,
    string Target,
    string EdgeType,
    string? Condition);

public sealed class Dag
{
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; }
    public IReadOnlyList<WorkflowEdge> Edges { get; }

    // For each node id, the outgoing edges. Lookup is O(1); the common
    // worker-side operation is "given a step I just finished, what do I
    // enqueue next?".
    public IReadOnlyDictionary<string, IReadOnlyList<WorkflowEdge>> Adjacency { get; }

    // Incoming-edge count per node. Used to find start nodes and (in
    // Sprint 2.4) to schedule a step only once all its predecessors have
    // reported back.
    public IReadOnlyDictionary<string, int> InDegree { get; }

    internal Dag(
        IReadOnlyDictionary<string, WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowEdge>> adjacency,
        IReadOnlyDictionary<string, int> inDegree)
    {
        Nodes = nodes;
        Edges = edges;
        Adjacency = adjacency;
        InDegree = inDegree;
    }

    // Entry points of the workflow — nodes with no incoming edges. More
    // than one is allowed; the executor starts all of them in parallel.
    public WorkflowNode[] StartNodes() =>
        Nodes.Values.Where(n => InDegree[n.Id] == 0).ToArray();

    // Nodes to schedule after `nodeId` finishes with outcome `result`.
    // `result` is "success" or "failure"; `always` edges fire on both.
    // `conditional` edges are skipped here — the executor resolves them
    // via the condition evaluator before asking for the next node.
    public WorkflowNode[] NextNodes(string nodeId, string result)
    {
        if (!Adjacency.TryGetValue(nodeId, out var outgoing) || outgoing.Count == 0)
            return Array.Empty<WorkflowNode>();

        return outgoing
            .Where(e => EdgeFires(e, result))
            .Select(e => Nodes[e.Target])
            .ToArray();
    }

    private static bool EdgeFires(WorkflowEdge edge, string result) =>
        edge.EdgeType == "always" || edge.EdgeType == result;
}
