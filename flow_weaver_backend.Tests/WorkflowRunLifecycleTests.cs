using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.WorkflowRun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// Run lifecycle from the API's side: list, inspect, soft-delete and cancel.
//
// Cancel is the one with teeth. An operator hits it because something is going
// wrong on real devices, so it has to (a) refuse on a run that already
// finished — pretending otherwise hides the real outcome — and (b) actually
// stop the work: in-flight steps cancelled AND pending queue jobs dropped, or
// a worker picks the next step up seconds later.
public class WorkflowRunLifecycleTests
{

    // The three set-based statements are `virtual` on the repository precisely
    // so they can be substituted here; the InMemory provider has no
    // ExecuteUpdateAsync. Row-by-row equivalents keep the observable effect
    // identical.
    private sealed class InMemoryRunRepository : WorkflowRunRepository
    {
        private readonly AppDbContext _db;
        public InMemoryRunRepository(AppDbContext db) : base(db) => _db = db;

        public override async Task<int> SoftDeleteStepsByRunAsync(
            Guid runId, DateTime now, CancellationToken ct = default)
        {
            var rows = await _db.StepRuns
                .Where(s => s.WorkflowRunId == runId && s.IsActive)
                .ToListAsync(ct);
            foreach (var s in rows) { s.IsActive = false; s.UpdatedAt = now; }
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }

        public override async Task<(int runs, int steps)> SoftDeleteAllAsync(
            DateTime now, CancellationToken ct = default)
        {
            var runs = await _db.WorkflowRuns
                .Where(r => r.IsActive).ToListAsync(ct);
            foreach (var r in runs) { r.IsActive = false; r.UpdatedAt = now; }
            var steps = await _db.StepRuns
                .Where(s => s.IsActive).ToListAsync(ct);
            foreach (var s in steps) { s.IsActive = false; s.UpdatedAt = now; }
            await _db.SaveChangesAsync(ct);
            return (runs.Count, steps.Count);
        }

        public override async Task<int> CancelInFlightStepsByRunAsync(
            Guid runId, DateTime now, CancellationToken ct = default)
        {
            var rows = await _db.StepRuns
                .Where(s => s.WorkflowRunId == runId && s.IsActive && (s.Status == StepStatus.Pending || s.Status == StepStatus.Running))
                .ToListAsync(ct);
            foreach (var s in rows)
            {
                s.Status = StepStatus.Cancelled;
                s.CompletedAt = now;
                s.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeQueue Queue { get; } = new();

        public WorkflowRunService Build() => new(
            new InMemoryRunRepository(Db),
            new FakeUser(),
            Queue,
            new FakeAudit(),
            NullLogger<WorkflowRunService>.Instance);

        public Guid SeedRun(
            string status = RunStatus.Running, bool active = true, string trigger = "manual")
        {
            var id = Guid.NewGuid();
            Db.WorkflowRuns.Add(new WorkflowRunModel
            {
                WorkflowRunId = id,
                WorkflowId = Guid.NewGuid(),
                Status = status,
                Trigger = trigger,
                CreatedBy = "tester",
                InputPayload = TestJson.Element("{}"),
                TargetDevices = new List<Guid>(),
                TargetPools = new List<Guid>(),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedStep(Guid runId, string status = StepStatus.Pending)
        {
            var id = Guid.NewGuid();
            Db.StepRuns.Add(new StepRun
            {
                StepRunId = id,
                WorkflowRunId = runId,
                NodeId = "a",
                Status = status,
                Error = "",
                InputPayload = TestJson.Element("{}"),
                OutputPayload = TestJson.Element("{}"),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static ListResponse<WorkflowRunResponse> Page(
        ActionResult<ListResponse<WorkflowRunResponse>> result)
        => Assert.IsType<ListResponse<WorkflowRunResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    private static WorkflowRunResponse Body(ActionResult<WorkflowRunResponse> result)
        => result.Value ?? Assert.IsType<WorkflowRunResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    // ─── Read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ExcludesSoftDeletedRuns()
    {
        using var f = new Fixture();
        f.SeedRun();
        f.SeedRun(active: false);

        var page = Page(await f.Build().GetAsync());

        Assert.Equal(1, page.Total);
        Assert.Single(page.Data);
    }

    [Fact]
    public async Task List_ClampsThePageSize()
    {
        using var f = new Fixture();
        f.SeedRun();
        f.SeedRun();

        Assert.Single(Page(await f.Build().GetAsync(limit: 1)).Data);
    }

    [Fact]
    public async Task GetById_ReturnsTheRunWithItsProvenance()
    {
        using var f = new Fixture();
        var id = f.SeedRun(trigger: "schedule");

        var body = Body(await f.Build().GetByIdAsync(id));

        Assert.Equal(id, body.Id);
        Assert.Equal("schedule", body.Trigger);
        Assert.Equal("tester", body.CreatedBy);
    }

    [Fact]
    public async Task GetById_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetByIdAsync(Guid.NewGuid())).Result);
    }

    // ─── Delete ─────────────────────────────────────────────────────────

    // Deleting a run has to take its steps with it, or the monitor/timeline
    // queries keep showing orphaned steps for a run that no longer exists.
    [Fact]
    public async Task Delete_CascadesToTheRunsSteps()
    {
        using var f = new Fixture();
        var runId = f.SeedRun();
        f.SeedStep(runId);
        f.SeedStep(runId, StepStatus.Completed);

        var result = await f.Build().DeleteAsync(runId);

        Assert.IsType<NoContentResult>(result);
        Assert.False(f.Db.WorkflowRuns.Single().IsActive);
        Assert.All(f.Db.StepRuns.ToList(), s => Assert.False(s.IsActive));
    }

    [Fact]
    public async Task Delete_LeavesOtherRunsStepsAlone()
    {
        using var f = new Fixture();
        var target = f.SeedRun();
        var other = f.SeedRun();
        f.SeedStep(target);
        var survivor = f.SeedStep(other);

        await f.Build().DeleteAsync(target);

        Assert.True(f.Db.StepRuns.Single(s => s.StepRunId == survivor).IsActive);
    }

    [Fact]
    public async Task Delete_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>(await f.Build().DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAll_ClearsRunsAndStepsAndReportsTheCounts()
    {
        using var f = new Fixture();
        var a = f.SeedRun();
        var b = f.SeedRun();
        f.SeedStep(a);
        f.SeedStep(b);

        var result = await f.Build().DeleteAllAsync();

        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        Assert.Equal(2, value.GetType().GetProperty("deleted")!.GetValue(value));
        Assert.Equal(2, value.GetType().GetProperty("steps_deleted")!.GetValue(value));
        Assert.All(f.Db.WorkflowRuns.ToList(), r => Assert.False(r.IsActive));
    }

    // ─── Cancel ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RunStatus.Pending)]
    [InlineData(RunStatus.Running)]
    public async Task Cancel_IsAllowedWhileTheRunIsInFlight(string status)
    {
        using var f = new Fixture();
        var id = f.SeedRun(status);

        var body = Body(await f.Build().CancelAsync(id));

        Assert.Equal(RunStatus.Cancelled, body.Status);
        var saved = f.Db.WorkflowRuns.Single();
        Assert.Equal(RunStatus.Cancelled, saved.Status);
        Assert.NotNull(saved.CompletedAt);
    }

    // Cancelling a finished run would pretend we did something; the 409 lets
    // the UI refresh and show the real final status.
    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public async Task Cancel_OnATerminalRunIs409(string status)
    {
        using var f = new Fixture();
        var id = f.SeedRun(status);

        var result = await f.Build().CancelAsync(id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(status, f.Db.WorkflowRuns.Single().Status);
    }

    // Cancelling must actually stop the work: in-flight steps terminalised…
    [Fact]
    public async Task Cancel_TerminalisesTheInFlightSteps()
    {
        using var f = new Fixture();
        var runId = f.SeedRun();
        var pending = f.SeedStep(runId, StepStatus.Pending);
        var running = f.SeedStep(runId, StepStatus.Running);
        var done = f.SeedStep(runId, StepStatus.Completed);

        await f.Build().CancelAsync(runId);

        Assert.Equal(StepStatus.Cancelled, f.Db.StepRuns.Single(s => s.StepRunId == pending).Status);
        Assert.Equal(StepStatus.Cancelled, f.Db.StepRuns.Single(s => s.StepRunId == running).Status);
        // A step that already finished keeps its real outcome.
        Assert.Equal(StepStatus.Completed, f.Db.StepRuns.Single(s => s.StepRunId == done).Status);
    }

    // …and pending queue jobs dropped, or a worker picks the next step up
    // seconds after the operator hit cancel.
    [Fact]
    public async Task Cancel_DropsThePendingQueueJobs()
    {
        using var f = new Fixture();
        var runId = f.SeedRun();

        await f.Build().CancelAsync(runId);

        Assert.Contains(runId, f.Queue.Cancelled);
    }

    [Fact]
    public async Task Cancel_AnUnknownIdIs404()
    {
        using var f = new Fixture();

        Assert.IsType<NotFoundObjectResult>((await f.Build().CancelAsync(Guid.NewGuid())).Result);
    }

    // Another run's steps must not be swept up by a cancel.
    [Fact]
    public async Task Cancel_LeavesOtherRunsStepsAlone()
    {
        using var f = new Fixture();
        var target = f.SeedRun();
        var other = f.SeedRun();
        var survivor = f.SeedStep(other, StepStatus.Running);

        await f.Build().CancelAsync(target);

        Assert.Equal(StepStatus.Running, f.Db.StepRuns.Single(s => s.StepRunId == survivor).Status);
    }
}
