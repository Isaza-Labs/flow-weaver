using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools.Handlers;

namespace flow_weaver_backend.Tests.Conformance;

// The hashes this system has already computed and stored, pinned as literals.
//
// `schema_hash` is the identity of a graph. A `SimulationResult` row is matched to the graph
// it simulated by comparing it, and a promotion gate that has been passed stays passed only
// while it keeps agreeing. So the acceptance criterion for MOVING the canonicalizer is not
// that the suite stays green — it is that every one of these strings is still produced.
//
// Captured from `SimulateWorkflowRunHandler.ComputeSchemaHash` BEFORE the extraction. If one
// changes afterwards, the move was not behaviour-preserving: revert it. Do not re-capture the
// literals to match — that turns the only check on the property into a record of whatever the
// code now does.
public class CanonicalHashGoldenTests
{
    private static string Hash(string nodes, string edges)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse(edges);
        return SimulateWorkflowRunHandler.ComputeSchemaHash(n.RootElement, e.RootElement);
    }

    // The corpus deliberately includes the pairs that must NOT collide and the pairs that
    // must: property order and whitespace are insignificant, array order is not.
    public static TheoryData<string, string, string> Corpus() => new()
    {
        { "empty", "[]", "[]" },
        { "single-node", """[{"id":"a","snippet_id":"__start__"}]""", "[]" },
        {
            "two-nodes-one-edge",
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]""",
            """[{"source":"a","target":"b","type":"success"}]"""
        },
        {
            "subflow-parent",
            """[{"id":"a","snippet_id":"subflow","type":"subflow","config_overrides":{"workflow":"child"}}]""",
            "[]"
        },
        {
            "numeric-config",
            """[{"id":"a","snippet_id":"__start__","config_overrides":{"one":1,"onePointZero":1.0,"onePointZeroZero":1.00}}]""",
            "[]"
        },
        {
            "coordinates",
            """[{"id":"a","snippet_id":"__start__","x":10.5,"y":-3}]""",
            "[]"
        },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void The_corpus_hashes_are_unchanged(string name, string nodes, string edges)
    {
        var actual = Hash(nodes, edges);
        // Assert.True rather than Assert.Equal: on failure the whole hash has to be readable,
        // and Equal elides the middle of a long string. Whoever sees this fail needs the value
        // to decide whether the move was behaviour-preserving, not an ellipsis.
        Assert.True(Golden[name] == actual, $"""["{name}"] = "{actual}",""");
    }

    // Captured 2026-08-28 from the unmodified `SimulateWorkflowRunHandler.ComputeSchemaHash`,
    // at flow-weaver fe07550, before the canonicalizer was moved anywhere.
    private static readonly Dictionary<string, string> Golden = new(StringComparer.Ordinal)
    {
        ["empty"] = "42ba610a86d150941363115a6aad91c30c4d108039d4f16ca498999e39039a06",
        ["single-node"] = "1cadf7a2165e1caaae1772a599721c1ed23a9579a3a56777a7a7336a462e5909",
        ["two-nodes-one-edge"] = "3c24556770a2388a1e22a6318060c52145bfe8510c7aaafa913005536952c6af",
        ["subflow-parent"] = "09c619bbe4dee3336e43171ef743e86ab70b354b46470fad384944d09633d9f0",
        ["numeric-config"] = "3466e185afd12b8e67b96d87e36b60a199f1ed82835dcf19970067c7231ade44",
        ["coordinates"] = "9e2f4d52c4f8c345e59f2f503d57aa82dc0873d81910558aed06dd5882b16e07",
    };

    // ── the properties the literals alone would not pin ──────────────────

    [Fact]
    public void Property_order_and_whitespace_do_not_change_the_hash()
    {
        Assert.Equal(
            Hash("""[{"id":"a","snippet_id":"__start__"}]""", "[]"),
            Hash("""[ { "snippet_id" : "__start__" ,   "id" : "a" } ]""", "[]"));
    }

    [Fact]
    public void Trailing_zeros_do_not_change_the_hash()
    {
        // Postgres jsonb strips them on storage, so an edit that changed nothing would
        // otherwise read as a changed graph after a round trip.
        Assert.Equal(
            Hash("""[{"id":"a","snippet_id":"__start__","config_overrides":{"n":1}}]""", "[]"),
            Hash("""[{"id":"a","snippet_id":"__start__","config_overrides":{"n":1.00}}]""", "[]"));
    }

    [Fact]
    public void Array_order_does_change_the_hash()
    {
        Assert.NotEqual(
            Hash("""[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]""", "[]"),
            Hash("""[{"id":"b","snippet_id":"__end__"},{"id":"a","snippet_id":"__start__"}]""", "[]"));
    }

    [Fact]
    public void Nodes_and_edges_are_not_interchangeable()
    {
        // The two are concatenated to form the canonical text. Without a separator between
        // them, content moved from one to the other could hash identically.
        Assert.NotEqual(
            Hash("""[{"id":"a","snippet_id":"__start__"}]""", "[]"),
            Hash("[]", """[{"id":"a","snippet_id":"__start__"}]"""));
    }
}
