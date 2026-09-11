using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Workflows saved before the canvas wrote real ids carry the editor marker
// "integration_action" in snippet_id. The engine resolves it at dispatch and the
// editor heals it on load, but a workflow nobody opens would stay broken until
// the next time it ran — and with the reference validator now rejecting the
// marker, it could not be re-saved without opening it first.
//
// This sweep removes the problem instead of waiting for someone to trip over it.
public class LegacyNodeMarkerBackfillTests
{
    private static Guid SeedTargetSnippet(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = id, Name = "integration_action", Type = "integration_action",
            TargetMode = "once", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedWorkflow(AppDbContext db, string nodesJson)
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = id, Name = "wf", Environment = "draft",
            Nodes = JsonDocument.Parse(nodesJson).RootElement.Clone(),
            Edges = JsonDocument.Parse("[]").RootElement.Clone(),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static string SnippetIdOf(AppDbContext db, Guid workflowId, string nodeId)
        => db.Workflows.Single(w => w.WorkflowId == workflowId).Nodes
            .EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == nodeId)
            .GetProperty("snippet_id").GetString()!;

    [Fact]
    public async Task The_marker_is_rewritten_to_the_seeded_snippet_id()
    {
        using var db = TestDb.NewContext();
        var target = SeedTargetSnippet(db);
        var wf = SeedWorkflow(db, """
            [{ "id": "__start__", "snippet_id": "__start__" },
             { "id": "node-1", "snippet_id": "integration_action",
               "config_overrides": { "action_id": "x", "integration_name": "Action1" } }]
            """);

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal(target.ToString(), SnippetIdOf(db, wf, "node-1"));
    }

    [Fact]
    public async Task Everything_else_on_the_node_survives()
    {
        // The rewrite copies the document through; losing config_overrides
        // would silently unwire the action it points at.
        using var db = TestDb.NewContext();
        SeedTargetSnippet(db);
        var wf = SeedWorkflow(db, """
            [{ "id": "node-1", "snippet_id": "integration_action", "x": 3, "y": 72,
               "config_overrides": { "method": "GET", "path": "/logs/{orgId}",
                                     "integration_name": "Action1" } }]
            """);

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        var node = db.Workflows.Single().Nodes.EnumerateArray().Single();
        Assert.Equal(3, node.GetProperty("x").GetInt32());
        var overrides = node.GetProperty("config_overrides");
        Assert.Equal("/logs/{orgId}", overrides.GetProperty("path").GetString());
        Assert.Equal("Action1", overrides.GetProperty("integration_name").GetString());
    }

    [Fact]
    public async Task Sentinels_and_real_ids_are_left_alone()
    {
        using var db = TestDb.NewContext();
        SeedTargetSnippet(db);
        var realId = Guid.NewGuid();
        var wf = SeedWorkflow(db, $$"""
            [{ "id": "__start__", "snippet_id": "__start__" },
             { "id": "sub", "snippet_id": "subflow" },
             { "id": "ping", "snippet_id": "{{realId}}" }]
            """);

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal("__start__", SnippetIdOf(db, wf, "__start__"));
        Assert.Equal("subflow", SnippetIdOf(db, wf, "sub"));
        Assert.Equal(realId.ToString(), SnippetIdOf(db, wf, "ping"));
    }

    [Fact]
    public async Task A_workflow_with_no_marker_is_not_touched()
    {
        // No spurious UpdatedAt: a sweep that dirties every row on every boot
        // would churn the audit trail and the "recently edited" ordering.
        using var db = TestDb.NewContext();
        SeedTargetSnippet(db);
        SeedWorkflow(db, """[{ "id": "__start__", "snippet_id": "__start__" }]""");
        var before = db.Workflows.Single().UpdatedAt;

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal(before, db.Workflows.Single().UpdatedAt);
    }

    [Fact]
    public async Task Running_twice_changes_nothing_the_second_time()
    {
        using var db = TestDb.NewContext();
        var target = SeedTargetSnippet(db);
        var wf = SeedWorkflow(db,
            """[{ "id": "node-1", "snippet_id": "integration_action" }]""");

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);
        var afterFirst = db.Workflows.Single().UpdatedAt;
        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal(target.ToString(), SnippetIdOf(db, wf, "node-1"));
        Assert.Equal(afterFirst, db.Workflows.Single().UpdatedAt);
    }

    [Fact]
    public async Task Version_snapshots_are_rewritten_too()
    {
        // A rollback restores this snapshot. Leaving it broken would put the
        // failure back the moment someone rolls back.
        using var db = TestDb.NewContext();
        var target = SeedTargetSnippet(db);
        db.WorkflowVersions.Add(new flow_weaver_backend.Models.WorkflowVersion
        {
            WorkflowVersionId = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), Version = 1,
            Nodes = JsonDocument.Parse(
                """[{ "id": "node-1", "snippet_id": "integration_action" }]""").RootElement.Clone(),
            Edges = JsonDocument.Parse("[]").RootElement.Clone(),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal(target.ToString(),
            db.WorkflowVersions.Single().Nodes.EnumerateArray().Single()
                .GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task Without_the_seeded_snippet_nothing_is_rewritten()
    {
        // There is no id to rewrite TO. Blanking or inventing one would be
        // worse than leaving the marker for the executor's shim to resolve.
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db,
            """[{ "id": "node-1", "snippet_id": "integration_action" }]""");

        await LegacyNodeMarkerBackfill.RunAsync(TestScopes.Over(db), NullLogger.Instance);

        Assert.Equal("integration_action", SnippetIdOf(db, wf, "node-1"));
    }
}
