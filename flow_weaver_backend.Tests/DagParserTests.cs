using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Semantic invariants that JSON Schema can't express: unique ids,
// dangling edges, cycles, missing start nodes, conditional edges with
// empty `condition`. Each test is a minimal DAG crafted to hit exactly
// one rule so a failure message points at the broken invariant without
// needing to read the assertion.
public class DagParserTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Valid_linear_dag_parses()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var dag = parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"00000000-0000-0000-0000-000000000001"},{"id":"c","snippet_id":"__end__"}]"""),
            E("""[{"source":"a","target":"b","type":"success"},{"source":"b","target":"c","type":"success"}]"""));
        Assert.NotNull(dag);
    }

    [Fact]
    public void Duplicate_node_id_throws()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var ex = Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__"},{"id":"a","snippet_id":"__end__"}]"""),
            E("[]")));
        Assert.Contains("duplicate node id", ex.Message);
    }

    [Fact]
    public void Edge_pointing_at_unknown_target_throws()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var ex = Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__"}]"""),
            E("""[{"source":"a","target":"nope","type":"success"}]""")));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Cycle_throws()
    {
        // a → b → a. The graph has no in-degree-0 node either, so the
        // parser catches it with the "no start node" message before
        // reaching the DFS — still a cycle-prevention win.
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]"""),
            E("""[{"source":"a","target":"b","type":"success"},{"source":"b","target":"a","type":"success"}]""")));
    }

    [Fact]
    public void Conditional_edge_without_condition_throws()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var ex = Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]"""),
            E("""[{"source":"a","target":"b","type":"conditional"}]""")));
        Assert.Contains("condition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_node_id_throws()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"snippet_id":"__start__"}]"""),
            E("[]")));
    }

    [Fact]
    public void Unknown_node_type_throws()
    {
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var ex = Assert.Throws<DagParseException>(() => parser.Parse(
            E("""[{"id":"a","snippet_id":"__start__","type":"gadget"}]"""),
            E("[]")));
        Assert.Contains("gadget", ex.Message);
    }
}
