using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Covers the static-analysis branches of the simulate handler. We
// pre-seed an InMemory db with a couple of workflows + snippets +
// integrations and then fire the handler with different `workflow_id`
// args. The assertions read the JSON `issues` / `warnings` arrays and
// check for the specific `kind` values — that keeps tests tied to the
// handler's public contract instead of its internal string formatting.
public class SimulateWorkflowRunHandlerTests
{
    private readonly FakeUser _caller = new();

    private AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private SimulateWorkflowRunHandler NewHandler(AppDbContext db) =>
        new(
            new RepositoryBase<Workflow>(db),
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new IntegrationActionRepository(db),
            new SimulationResultRepository(db),
            _caller,
            NullLogger<SimulateWorkflowRunHandler>.Instance);

    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    private static List<string> Kinds(JsonElement arr) => arr.ValueKind == JsonValueKind.Array
        ? arr.EnumerateArray()
            .Select(e => e.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList()
        : new();

    [Fact]
    public async Task Missing_workflow_id_returns_error()
    {
        using var db = NewContext(nameof(Missing_workflow_id_returns_error));
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(E("{}"), default);

        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Missing_workflow_row_returns_error()
    {
        using var db = NewContext(nameof(Missing_workflow_row_returns_error));
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(
            E($$"""{"workflow_id":"{{Guid.NewGuid()}}"}"""), default);

        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Well_formed_workflow_is_ok()
    {
        using var db = NewContext(nameof(Well_formed_workflow_is_ok));
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new flow_weaver_backend.Models.Snippet
        {
            SnippetId = snippetId,
            Name = "ping", Type = "ping", TargetMode = "once",
            TimeoutSeconds = 30, MaxParallel = 10, Verified = true,
            InputSchema = E("{}"), OutputSchema = E("{}"), RetryPolicy = E("{}"),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new flow_weaver_backend.Models.Workflow
        {
            WorkflowId = wfId,            Name = "wf", Version = 1, Environment = "draft",
            Nodes = E($$"""[{"id":"__start__","snippet_id":"__start__"},{"id":"a","snippet_id":"{{snippetId}}"},{"id":"__end__","snippet_id":"__end__"}]"""),
            Edges = E("""[{"source":"__start__","target":"a","type":"success"},{"source":"a","target":"__end__","type":"success"}]"""),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(E($$"""{"workflow_id":"{{wfId}}"}"""), default);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(0, result.GetProperty("issue_count").GetInt32());
    }

    [Fact]
    public async Task Missing_snippet_raises_issue()
    {
        using var db = NewContext(nameof(Missing_snippet_raises_issue));
        var orphanSnippetId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new flow_weaver_backend.Models.Workflow
        {
            WorkflowId = wfId, Name = "wf", Version = 1,
            Environment = "draft",
            Nodes = E($$"""[{"id":"__start__","snippet_id":"__start__"},{"id":"a","snippet_id":"{{orphanSnippetId}}"}]"""),
            Edges = E("""[{"source":"__start__","target":"a","type":"success"}]"""),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(E($$"""{"workflow_id":"{{wfId}}"}"""), default);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("snippet_not_found", Kinds(result.GetProperty("issues")));
    }

    [Fact]
    public async Task Literal_integration_action_snippet_id_flags_issue()
    {
        using var db = NewContext(nameof(Literal_integration_action_snippet_id_flags_issue));
        var wfId = Guid.NewGuid();
        db.Workflows.Add(new flow_weaver_backend.Models.Workflow
        {
            WorkflowId = wfId, Name = "wf", Version = 1,
            Environment = "draft",
            // Classic mistake: agent wrote snippet_id: "integration_action"
            // instead of the UUID of the seeded snippet.
            Nodes = E("""[{"id":"a","snippet_id":"integration_action"}]"""),
            Edges = E("[]"),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(E($$"""{"workflow_id":"{{wfId}}"}"""), default);

        Assert.Contains("literal_integration_action", Kinds(result.GetProperty("issues")));
    }

    [Fact]
    public async Task Template_pointing_to_downstream_node_raises_issue()
    {
        using var db = NewContext(nameof(Template_pointing_to_downstream_node_raises_issue));
        var sid = Guid.NewGuid();
        db.Snippets.Add(new flow_weaver_backend.Models.Snippet
        {
            SnippetId = sid,            Name = "t", Type = "transform", TargetMode = "once",
            TimeoutSeconds = 30, MaxParallel = 10, Verified = true,
            InputSchema = E("{}"), OutputSchema = E("{}"), RetryPolicy = E("{}"),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var wfId = Guid.NewGuid();
        // node `a` references steps.b.output.x but `b` runs AFTER `a`.
        // Avoid interpolation here — the `{{` ... `}}` in the template
        // is hard to escape inside a C# raw interpolated string, so we
        // build the JSON with plain concatenation instead.
        var nodesJson =
            "[{\"id\":\"a\",\"snippet_id\":\"" + sid + "\"," +
            "\"config_overrides\":{\"echo\":\"{{ steps.b.output.x }}\"}}," +
            "{\"id\":\"b\",\"snippet_id\":\"" + sid + "\"}]";
        db.Workflows.Add(new flow_weaver_backend.Models.Workflow
        {
            WorkflowId = wfId, Name = "wf", Version = 1,
            Environment = "draft",
            Nodes = E(nodesJson),
            Edges = E("""[{"source":"a","target":"b","type":"success"}]"""),
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var h = NewHandler(db);

        var result = await h.ExecuteAsync(E($$"""{"workflow_id":"{{wfId}}"}"""), default);

        Assert.Contains("template_refers_non_upstream", Kinds(result.GetProperty("issues")));
    }
}
