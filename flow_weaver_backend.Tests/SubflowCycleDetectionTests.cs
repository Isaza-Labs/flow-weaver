using System.Text.Json;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// The subflow-safety half. A workflow that
// references itself (directly or transitively) through subflow nodes must be
// rejected before any child run / ParentRunId link is created, and a subflow
// pointing at a non-existent workflow must fail loudly.
// DagParser.DetectSubflowCyclesAsync is the static guard EnqueueRunAsync runs.
//
// NOTE: runtime ParentRunId linkage, subflow input mapping, and error/cancel
// propagation to the parent step are execution-level concerns that need a live
// worker; they remain an E2E gap and are not asserted here.
public class SubflowCycleDetectionTests
{
    // A single subflow node pointing at `child`. Built by concatenation, not
    // interpolation: the nested config_overrides object ends in `}}`, which
    // collides with `$$"""..."""` interpolation delimiters.
    private static JsonElement Subflow(Guid child) => JsonDocument.Parse(
        "[{\"id\":\"s\",\"snippet_id\":\"subflow\",\"config_overrides\":{\"subflow_workflow_id\":\""
        + child + "\"}}]").RootElement;

    // A leaf workflow with no subflow nodes.
    private static JsonElement Leaf() => JsonDocument.Parse(
        """[{"id":"a","snippet_id":"__start__"}]""").RootElement;

    [Fact]
    public async Task Direct_self_reference_is_a_cycle()
    {
        var root = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<DagParseException>(() =>
            DagParser.DetectSubflowCyclesAsync(
                root, Subflow(root), _ => Task.FromResult<JsonElement?>(Subflow(root))));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transitive_cycle_A_to_B_to_A_is_detected()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var aNodes = Subflow(b);
        var bNodes = Subflow(a);

        await Assert.ThrowsAsync<DagParseException>(() =>
            DagParser.DetectSubflowCyclesAsync(a, aNodes, id =>
                Task.FromResult<JsonElement?>(id == a ? aNodes : id == b ? bNodes : (JsonElement?)null)));
    }

    [Fact]
    public async Task Subflow_pointing_at_missing_workflow_throws()
    {
        var root = Guid.NewGuid();
        var missing = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<DagParseException>(() =>
            DagParser.DetectSubflowCyclesAsync(
                root, Subflow(missing), _ => Task.FromResult<JsonElement?>(null)));
        Assert.Contains(missing.ToString(), ex.Message);
    }

    [Fact]
    public async Task Acyclic_subflow_chain_passes()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // a → b, and b is a leaf (no further subflows). No cycle → no throw.
        await DagParser.DetectSubflowCyclesAsync(a, Subflow(b), id =>
            Task.FromResult<JsonElement?>(id == b ? Leaf() : (JsonElement?)null));
    }
}
