using System.Text.Json;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// Covers the server-side DAG layout helper. These are pure-function
// tests — no DB, no HTTP — so they run in <1s and shouldn't flake.
// Focus is on three invariants the frontend depends on:
//   1. Auto-layout skips when the user has arranged the diagram.
//   2. Auto-layout fires on agent-authored workflows (handles missing).
//   3. Handles are picked based on post-layout positions so edges
//      enter from the left, not the top.
public class WorkflowAutoLayoutTests
{
    [Fact]
    public void NeedsLayout_true_when_any_edge_has_no_handles()
    {
        var nodes = JsonDocument.Parse("""
        [
          {"id":"__start__","snippet_id":"__start__","x":0,"y":0},
          {"id":"n1","snippet_id":"00000000-0000-0000-0000-000000000001","x":200,"y":40},
          {"id":"__end__","snippet_id":"__end__","x":400,"y":0}
        ]
        """).RootElement;
        var edges = JsonDocument.Parse("""
        [
          {"source":"__start__","target":"n1","type":"success"},
          {"source":"n1","target":"__end__","type":"success"}
        ]
        """).RootElement;

        Assert.True(WorkflowAutoLayout.NeedsLayout(nodes, edges));
    }

    [Fact]
    public void NeedsLayout_false_when_handles_set_and_positions_vary()
    {
        var nodes = JsonDocument.Parse("""
        [
          {"id":"__start__","snippet_id":"__start__","x":0,"y":0},
          {"id":"n1","snippet_id":"00000000-0000-0000-0000-000000000001","x":200,"y":40},
          {"id":"n2","snippet_id":"00000000-0000-0000-0000-000000000002","x":400,"y":-40}
        ]
        """).RootElement;
        var edges = JsonDocument.Parse("""
        [
          {"source":"__start__","target":"n1","type":"success","source_handle":"s-right","target_handle":"t-left"},
          {"source":"n1","target":"n2","type":"success","source_handle":"s-right","target_handle":"t-left"}
        ]
        """).RootElement;

        Assert.False(WorkflowAutoLayout.NeedsLayout(nodes, edges));
    }

    [Fact]
    public void NeedsLayout_true_when_all_task_nodes_at_same_point()
    {
        // Agent writes everything at 0,0 by convention — even if edges
        // somehow carry handles, collapsed positions still want a layout.
        var nodes = JsonDocument.Parse("""
        [
          {"id":"__start__","snippet_id":"__start__","x":0,"y":0},
          {"id":"a","snippet_id":"00000000-0000-0000-0000-000000000001","x":0,"y":0},
          {"id":"b","snippet_id":"00000000-0000-0000-0000-000000000002","x":0,"y":0}
        ]
        """).RootElement;
        var edges = JsonDocument.Parse("""
        [
          {"source":"__start__","target":"a","type":"success","source_handle":"s-right","target_handle":"t-left"},
          {"source":"a","target":"b","type":"success","source_handle":"s-right","target_handle":"t-left"}
        ]
        """).RootElement;

        Assert.True(WorkflowAutoLayout.NeedsLayout(nodes, edges));
    }

    [Fact]
    public void Apply_assigns_distinct_x_per_rank_and_sets_handles()
    {
        var nodes = JsonDocument.Parse("""
        [
          {"id":"__start__","snippet_id":"__start__","x":0,"y":0},
          {"id":"a","snippet_id":"00000000-0000-0000-0000-000000000001","x":0,"y":0},
          {"id":"b","snippet_id":"00000000-0000-0000-0000-000000000002","x":0,"y":0},
          {"id":"__end__","snippet_id":"__end__","x":0,"y":0}
        ]
        """).RootElement;
        var edges = JsonDocument.Parse("""
        [
          {"source":"__start__","target":"a","type":"success"},
          {"source":"a","target":"b","type":"success"},
          {"source":"b","target":"__end__","type":"success"}
        ]
        """).RootElement;

        var (laidNodes, laidEdges) = WorkflowAutoLayout.Apply(nodes, edges);

        // Each node landed in a distinct column.
        var xs = laidNodes.EnumerateArray()
            .Select(n => n.GetProperty("x").GetDouble())
            .ToList();
        Assert.Equal(4, xs.Distinct().Count());

        // Every edge picked up a handle — the main reason we run Apply.
        foreach (var e in laidEdges.EnumerateArray())
        {
            Assert.True(e.TryGetProperty("source_handle", out var sh)
                && sh.GetString()!.StartsWith('s'));
            Assert.True(e.TryGetProperty("target_handle", out var th)
                && th.GetString()!.StartsWith('t'));
        }
    }

    [Fact]
    public void Apply_preserves_unrelated_node_properties()
    {
        // config_overrides, type, and any other metadata must survive
        // the JSON round-trip — the layout pass only manages x/y.
        var nodes = JsonDocument.Parse("""
        [
          {"id":"__start__","snippet_id":"__start__","x":0,"y":0},
          {"id":"a","snippet_id":"00000000-0000-0000-0000-000000000001","x":0,"y":0,
           "type":"task",
           "config_overrides":{"timeout_seconds":99,"nested":{"key":["v1","v2"]}}},
          {"id":"__end__","snippet_id":"__end__","x":0,"y":0}
        ]
        """).RootElement;
        var edges = JsonDocument.Parse("""
        [
          {"source":"__start__","target":"a","type":"success"},
          {"source":"a","target":"__end__","type":"success"}
        ]
        """).RootElement;

        var (laidNodes, _) = WorkflowAutoLayout.Apply(nodes, edges);

        var task = laidNodes.EnumerateArray()
            .First(n => n.GetProperty("id").GetString() == "a");
        Assert.Equal("task", task.GetProperty("type").GetString());
        Assert.Equal(99, task.GetProperty("config_overrides").GetProperty("timeout_seconds").GetInt32());
        Assert.Equal("v1",
            task.GetProperty("config_overrides")
                .GetProperty("nested")
                .GetProperty("key")[0].GetString());
    }
}
