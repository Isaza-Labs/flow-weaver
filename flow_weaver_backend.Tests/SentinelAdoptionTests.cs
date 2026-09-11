using System.Text.Json;
using flow_weaver_backend.Services.Workflow;

namespace flow_weaver_backend.Tests;

// A workflow created here is born with a start and an end whose NODE id is the
// sentinel. An imported graph brings its own — Nashira writes
// `{"id":"start","snippet_id":"__start__"}`, which the engine accepts because it
// has always identified a sentinel by `snippet_id`.
//
// Valid, and still wrong for an import: the receiving workflow already has the
// pair the product made, so a graph that brings its own leaves four, and the two
// it brought are the only ones the edges reach. The ends are adopted instead.
public class SentinelAdoptionTests
{
    private static JsonElement P(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly string ImportedNodes = """
        [
          { "id": "start",        "snippet_id": "__start__", "x": 0,   "y": 0 },
          { "id": "ping_devices", "snippet_id": "7fe97360-0b6c-459d-a57e-1f107aee3907", "x": 220, "y": 0 },
          { "id": "end",          "snippet_id": "__end__",   "x": 440, "y": 0 }
        ]
        """;

    private static readonly string ImportedEdges = """
        [
          { "source": "start",        "target": "ping_devices", "type": "success" },
          { "source": "ping_devices", "target": "end",          "type": "success" }
        ]
        """;

    private static List<string> Ids(JsonElement nodes) =>
        nodes.EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();

    private static List<(string, string)> Ends(JsonElement edges) =>
        edges.EnumerateArray()
            .Select(e => (e.GetProperty("source").GetString()!, e.GetProperty("target").GetString()!))
            .ToList();

    [Fact]
    public void The_ends_take_the_local_ids()
    {
        var (nodes, _) = NodeReferences.AdoptLocalSentinels(P(ImportedNodes), P(ImportedEdges));

        Assert.Equal(["__start__", "ping_devices", "__end__"], Ids(nodes));
    }

    [Fact]
    public void The_edges_follow_the_rename()
    {
        // The half that matters most: renaming the node without its edges leaves
        // the graph's ends pointing at ids nothing has, which is worse than the
        // duplicate pair it replaces.
        var (_, edges) = NodeReferences.AdoptLocalSentinels(P(ImportedNodes), P(ImportedEdges));

        Assert.Equal([("__start__", "ping_devices"), ("ping_devices", "__end__")], Ends(edges));
    }

    [Fact]
    public void Nothing_is_added_or_removed()
    {
        var (nodes, edges) = NodeReferences.AdoptLocalSentinels(P(ImportedNodes), P(ImportedEdges));

        Assert.Equal(3, nodes.GetArrayLength());
        Assert.Equal(2, edges.GetArrayLength());
    }

    [Fact]
    public void Everything_else_on_the_node_survives()
    {
        var (nodes, _) = NodeReferences.AdoptLocalSentinels(P(ImportedNodes), P(ImportedEdges));
        var start = nodes.EnumerateArray().First();

        Assert.Equal("__start__", start.GetProperty("snippet_id").GetString());
        Assert.Equal(0, start.GetProperty("x").GetInt32());
    }

    [Fact]
    public void A_graph_already_using_the_local_ids_is_untouched()
    {
        var native = P("""
            [
              { "id": "__start__", "snippet_id": "__start__" },
              { "id": "n1",        "snippet_id": "7fe97360-0b6c-459d-a57e-1f107aee3907" },
              { "id": "__end__",   "snippet_id": "__end__" }
            ]
            """);
        var edges = P("""[{ "source": "__start__", "target": "n1", "type": "success" }]""");

        var (nodes, outEdges) = NodeReferences.AdoptLocalSentinels(native, edges);

        Assert.Equal(["__start__", "n1", "__end__"], Ids(nodes));
        Assert.Equal([("__start__", "n1")], Ends(outEdges));
    }

    [Fact]
    public void A_graph_with_no_sentinels_is_untouched()
    {
        var nodesIn = P("""[{ "id": "a", "snippet_id": "7fe97360-0b6c-459d-a57e-1f107aee3907" }]""");
        var edgesIn = P("[]");

        var (nodes, edges) = NodeReferences.AdoptLocalSentinels(nodesIn, edgesIn);

        Assert.Equal(["a"], Ids(nodes));
        Assert.Empty(edges.EnumerateArray());
    }

    [Fact]
    public void A_second_start_is_left_alone_rather_than_merged()
    {
        // Two start nodes is a malformed graph. Collapsing them onto one id would
        // hide it; leaving them lets the reference validator refuse the workflow,
        // which is the outcome that tells someone what is wrong.
        var nodesIn = P("""
            [
              { "id": "start",  "snippet_id": "__start__" },
              { "id": "start2", "snippet_id": "__start__" },
              { "id": "end",    "snippet_id": "__end__" }
            ]
            """);

        var (nodes, _) = NodeReferences.AdoptLocalSentinels(nodesIn, P("[]"));

        Assert.Equal(["__start__", "start2", "__end__"], Ids(nodes));
    }

    [Fact]
    public void An_occupied_local_id_blocks_the_adoption()
    {
        // Some other node already answers to `__start__`. Renaming onto it would
        // produce two nodes with one id, which is worse than leaving the graph as
        // it arrived for validation to reject.
        var nodesIn = P("""
            [
              { "id": "__start__", "snippet_id": "7fe97360-0b6c-459d-a57e-1f107aee3907" },
              { "id": "start",     "snippet_id": "__start__" }
            ]
            """);

        var (nodes, _) = NodeReferences.AdoptLocalSentinels(nodesIn, P("[]"));

        Assert.Equal(["__start__", "start"], Ids(nodes));
    }
}
