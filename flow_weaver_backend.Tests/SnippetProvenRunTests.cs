using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// `completed_run_count` is what separates a reusable building block from a
// half-finished draft. The workflow editor palette reads it to file
// never-completed snippets under "Unproven" instead of padding the list with
// experiments that never worked.
//
// The count is derived from step_runs on every read rather than stored on the
// snippet, because runs happen in places that have no business writing back to
// the snippet row (the scheduler, a webhook ingest, another user's session).
public class SnippetProvenRunTests
{
    private static SnippetService NewSvc(AppDbContext db)
        => new(new SnippetRepository(db), new StepRunRepository(db), new FakeUser(),
               new FakeAudit(), new FakeTrace(), NullLogger<SnippetService>.Instance);

    private static Guid SeedSnippet(AppDbContext db, string name = "s")
    {
        var id = Guid.NewGuid();
        db.Set<SnippetModel>().Add(new SnippetModel
        {
            SnippetId = id,
            Name = name,
            Type = "rest_call",
            TargetMode = "per_device",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static void SeedStep(
        AppDbContext db, Guid snippetId, string status,
        DateTime? completedAt = null, bool isActive = true)
    {
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "n1",
            SnippetId = snippetId,
            Status = status,
            CompletedAt = completedAt,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static SnippetResponse Single(ActionResult<ListResponse<SnippetResponse>> r)
        => Assert.Single(((ListResponse<SnippetResponse>)((OkObjectResult)r.Result!).Value!).Data);

    [Fact]
    public async Task A_snippet_that_never_ran_is_unproven()
    {
        using var db = TestDb.NewContext();
        SeedSnippet(db);

        var row = Single(await NewSvc(db).GetAsync());

        Assert.Equal(0, row.CompletedRunCount);
        Assert.Null(row.LastCompletedRunAt);
    }

    [Fact]
    public async Task A_completed_step_proves_the_snippet()
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        var at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedStep(db, id, StepStatus.Completed, at);

        var row = Single(await NewSvc(db).GetAsync());

        Assert.Equal(1, row.CompletedRunCount);
        Assert.Equal(at, row.LastCompletedRunAt);
    }

    // The distinction the user asked for: a snippet that ran and always failed
    // is exactly the kind that should NOT be offered as a reusable block.
    [Theory]
    [InlineData(StepStatus.Failed)]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData(StepStatus.Skipped)]
    [InlineData(StepStatus.Cancelled)]
    public async Task A_step_that_did_not_complete_does_not_prove_the_snippet(string status)
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        SeedStep(db, id, status);

        Assert.Equal(0, Single(await NewSvc(db).GetAsync()).CompletedRunCount);
    }

    [Fact]
    public async Task Completed_steps_are_counted_and_the_latest_wins()
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        var older = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedStep(db, id, StepStatus.Completed, newer);
        SeedStep(db, id, StepStatus.Completed, older);
        SeedStep(db, id, StepStatus.Failed);

        var row = Single(await NewSvc(db).GetAsync());

        Assert.Equal(2, row.CompletedRunCount);
        Assert.Equal(newer, row.LastCompletedRunAt);
    }

    // Deleting a run is housekeeping on the run log, not a statement that the
    // snippet never worked. If soft-deleted steps stopped counting, a retention
    // sweep would silently demote proven snippets back into "Unproven" months
    // later — the palette would churn for reasons no user could see.
    [Fact]
    public async Task A_soft_deleted_step_still_counts()
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        SeedStep(db, id, StepStatus.Completed, DateTime.UtcNow, isActive: false);

        Assert.Equal(1, Single(await NewSvc(db).GetAsync()).CompletedRunCount);
    }

    [Fact]
    public async Task Counts_do_not_bleed_between_snippets()
    {
        using var db = TestDb.NewContext();
        var proven = SeedSnippet(db, "proven");
        var draft = SeedSnippet(db, "draft");
        SeedStep(db, proven, StepStatus.Completed, DateTime.UtcNow);

        var list = ((ListResponse<SnippetResponse>)
            ((OkObjectResult)(await NewSvc(db).GetAsync()).Result!).Value!).Data;

        Assert.Equal(1, list.Single(s => s.SnippetId == proven).CompletedRunCount);
        Assert.Equal(0, list.Single(s => s.SnippetId == draft).CompletedRunCount);
    }

    [Fact]
    public async Task GetById_carries_the_same_count()
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        SeedStep(db, id, StepStatus.Completed, DateTime.UtcNow);

        var body = Assert.IsType<SnippetResponse>((await NewSvc(db).GetByIdAsync(id)).Value);

        Assert.Equal(1, body.CompletedRunCount);
    }

    // A freshly created snippet reports 0 rather than inheriting a count from
    // nowhere — the palette relies on that to route it to "Unproven".
    [Fact]
    public async Task A_newly_created_snippet_reports_zero()
    {
        using var db = TestDb.NewContext();
        var created = await NewSvc(db).PostAsync(new CreateSnippet
        {
            Name = "brand-new", Type = "rest_call", TargetMode = "per_device",
        });

        var body = Assert.IsType<SnippetResponse>(
            Assert.IsType<CreatedAtActionResult>(created.Result).Value);

        Assert.Equal(0, body.CompletedRunCount);
        Assert.Null(body.LastCompletedRunAt);
    }

    // A step row with no snippet (inline node config) must not be attributed to
    // some other snippet, and must not blow up the GROUP BY.
    [Fact]
    public async Task Steps_with_no_snippet_are_ignored()
    {
        using var db = TestDb.NewContext();
        var id = SeedSnippet(db);
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "inline",
            SnippetId = null,
            Status = StepStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        Assert.Equal(0, Single(await NewSvc(db).GetAsync()).CompletedRunCount);
    }

    [Fact]
    public async Task An_empty_snippet_list_asks_for_no_stats()
    {
        // Guards the early return in GetCompletedStatsBySnippetAsync: an empty
        // `IN ()` is a query some providers refuse outright.
        using var db = TestDb.NewContext();
        var stats = await new StepRunRepository(db)
            .GetCompletedStatsBySnippetAsync(Array.Empty<Guid>());

        Assert.Empty(stats);
    }
}
