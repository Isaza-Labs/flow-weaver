using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// A workflow with an integration_action node failed at RUN time with
// "node `node-1` has invalid snippet_id", and the run reported nothing the UI
// could show. Three layers were complicit:
//
//   1. the canvas stored its own editor marker — the literal string
//      "integration_action" — in snippet_id and saved it verbatim;
//   2. the reference validator skipped EVERY non-GUID snippet_id as "a
//      sentinel, handled elsewhere". That marker was not handled anywhere, so
//      the workflow saved cleanly;
//   3. the engine accepts only a GUID or one of its own sentinels there, threw,
//      and the orchestration failure was recorded nowhere but the worker log.
//
// The engine's sentinels and the validator's allow-list have to agree, or a
// value one accepts and the other doesn't reproduces exactly this.
public class IntegrationActionNodeIdTests
{
    // `effective` stays null: a snippet_id that never resolves is rejected long
    // before the per-resource permission check, same as the other guard-path
    // fixtures in this suite.
    private static WorkflowReferenceValidator Validator(AppDbContext db)
        => new(new SnippetRepository(db), new IntegrationRepository(db),
               new RepositoryBase<IntegrationAction>(db), new RepositoryBase<McpServer>(db),
               effective: null!, appSettings: new FakeAppSettings(),
               logger: NullLogger<WorkflowReferenceValidator>.Instance);

    private static JsonElement Nodes(string snippetId) => JsonDocument.Parse($$"""
        [
          { "id": "__start__", "snippet_id": "__start__" },
          { "id": "node-1", "snippet_id": "{{snippetId}}",
            "config_overrides": { "integration_id": "{{Guid.Empty}}", "action_id": "{{Guid.Empty}}" } },
          { "id": "__end__", "snippet_id": "__end__" }
        ]
        """).RootElement.Clone();

    [Fact]
    public async Task The_editor_marker_is_rejected_at_save_naming_the_node()
    {
        // The exact shape the reported workflow was stored with. It must not
        // reach the database again.
        using var db = TestDb.NewContext();

        var result = await Validator(db).ValidateAsync(Nodes("integration_action"), default);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("node-1", error);
        Assert.Contains("integration_action", error);
        // Actionable: it has to say what to do, not just what is wrong.
        Assert.Contains("Re-select", error);
    }

    [Theory]
    [InlineData("__start__")]
    [InlineData("__end__")]
    [InlineData("subflow")]
    public async Task The_engines_real_sentinels_still_pass(string sentinel)
    {
        // These are the only non-GUID values EnqueueStepAsync handles. Rejecting
        // one here would break every workflow that uses it.
        using var db = TestDb.NewContext();
        var nodes = JsonDocument.Parse($$"""
            [{ "id": "n", "snippet_id": "{{sentinel}}" }]
            """).RootElement.Clone();

        Assert.True((await Validator(db).ValidateAsync(nodes, default)).IsValid);
    }

    [Theory]
    [InlineData("mcp_call")]      // the other virtual type — also a real snippet
    [InlineData("python_snippet")]
    [InlineData("node-1")]        // a node id pasted into the wrong field
    [InlineData("TODO")]
    public async Task Any_other_non_guid_id_is_rejected(string bogus)
    {
        using var db = TestDb.NewContext();

        var result = await Validator(db).ValidateAsync(
            JsonDocument.Parse($$"""[{ "id": "n", "snippet_id": "{{bogus}}" }]""").RootElement.Clone(),
            default);

        Assert.False(result.IsValid);
        Assert.Contains(bogus, Assert.Single(result.Errors));
    }

    [Fact]
    public async Task A_real_snippet_id_passes()
    {
        using var db = TestDb.NewContext();
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = id, Name = "ping", Type = "ping", TargetMode = "once",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var nodes = JsonDocument.Parse($$"""[{ "id": "n", "snippet_id": "{{id}}" }]""").RootElement.Clone();

        Assert.True((await Validator(db).ValidateAsync(nodes, default)).IsValid);
    }

    [Fact]
    public async Task A_guid_that_matches_no_snippet_is_still_rejected()
    {
        // Pre-existing behaviour that the new non-GUID branch must not shadow:
        // returning early on the sentinel check would skip this entirely.
        using var db = TestDb.NewContext();
        var ghost = Guid.NewGuid();

        var result = await Validator(db).ValidateAsync(
            JsonDocument.Parse($$"""[{ "id": "n", "snippet_id": "{{ghost}}" }]""").RootElement.Clone(),
            default);

        Assert.False(result.IsValid);
        Assert.Contains(ghost.ToString(), Assert.Single(result.Errors));
    }
}
