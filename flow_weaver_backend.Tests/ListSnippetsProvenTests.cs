using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The agent picked snippets by name, with no way to tell a snippet that has
// actually run from a half-finished experiment — the same problem the editor
// palette had. `proven` and the proven-first ordering give it that signal.
//
// Advisory, never a filter: the agent creates a snippet and wires it up in the
// same turn, and that one is unproven by definition.
public class ListSnippetsProvenTests
{
    private static ListSnippetsHandler Handler(AppDbContext db)
        => new(new SnippetRepository(db), new FakeUser(), NullLogger<ListSnippetsHandler>.Instance);

    private static Guid Seed(AppDbContext db, string name, string type = "ping")
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = id, Name = name, Type = type, TargetMode = "once",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static void Complete(AppDbContext db, Guid snippetId, string status = StepStatus.Completed)
    {
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(), WorkflowRunId = Guid.NewGuid(), NodeId = "n",
            SnippetId = snippetId, Status = status, CompletedAt = DateTime.UtcNow,
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ToolDispatcher always passes a parsed object, never an undefined element.
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static List<JsonElement> Rows(JsonElement result)
        => result.EnumerateArray().ToList();

    [Fact]
    public async Task A_snippet_that_completed_a_run_is_proven()
    {
        using var db = TestDb.NewContext();
        Complete(db, Seed(db, "works"));

        var row = Assert.Single(Rows(await Handler(db).ExecuteAsync(NoArgs, default)));

        Assert.True(row.GetProperty("proven").GetBoolean());
    }

    [Theory]
    [InlineData(StepStatus.Failed)]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Cancelled)]
    public async Task A_snippet_that_never_completed_is_not_proven(string status)
    {
        // Ran and always failed is exactly the case the flag exists to expose.
        using var db = TestDb.NewContext();
        Complete(db, Seed(db, "broken"), status);

        var row = Assert.Single(Rows(await Handler(db).ExecuteAsync(NoArgs, default)));

        Assert.False(row.GetProperty("proven").GetBoolean());
    }

    [Fact]
    public async Task A_snippet_with_no_runs_is_not_proven()
    {
        using var db = TestDb.NewContext();
        Seed(db, "brand-new");

        Assert.False(Assert.Single(Rows(await Handler(db).ExecuteAsync(NoArgs, default)))
            .GetProperty("proven").GetBoolean());
    }

    [Fact]
    public async Task Proven_snippets_come_first_even_when_named_later()
    {
        // Ordering carries as much weight as the flag: the list is capped, and
        // the model reads top-down, so a working snippet must not be pushed
        // down (or off) by alphabetically-earlier drafts.
        using var db = TestDb.NewContext();
        Seed(db, "aaa-draft");
        Complete(db, Seed(db, "zzz-works"));

        var rows = Rows(await Handler(db).ExecuteAsync(NoArgs, default));

        Assert.Equal("zzz-works", rows[0].GetProperty("Name").GetString());
        Assert.Equal("aaa-draft", rows[1].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Unproven_snippets_are_still_listed()
    {
        // Not a filter. The agent creates a snippet and uses it in the same
        // turn; hiding it would break the flow this tool is most used for.
        using var db = TestDb.NewContext();
        Seed(db, "only-a-draft");

        Assert.Single(Rows(await Handler(db).ExecuteAsync(NoArgs, default)));
    }

    [Fact]
    public async Task The_type_filter_still_applies()
    {
        using var db = TestDb.NewContext();
        Complete(db, Seed(db, "a-ping", "ping"));
        Seed(db, "a-rest", "rest_call");

        var args = JsonDocument.Parse("""{"type":"rest_call"}""").RootElement;
        var row = Assert.Single(Rows(await Handler(db).ExecuteAsync(args, default)));

        Assert.Equal("a-rest", row.GetProperty("Name").GetString());
    }

    [Fact]
    public void The_tool_description_tells_the_model_what_proven_means()
    {
        // The model only knows what the description says. If it does not
        // explain the flag and that a same-turn creation is legitimately
        // unproven, the signal is noise.
        using var db = TestDb.NewContext();
        var description = Handler(db).Description;

        Assert.Contains("proven", description);
        Assert.Contains("PREFER", description);
        // ...and that a snippet the agent just created is legitimately unproven,
        // so it does not read the flag as a reason to avoid its own work.
        Assert.Contains("in this turn", description);
    }
}
