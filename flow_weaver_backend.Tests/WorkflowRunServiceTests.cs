using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowRun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// NOTE: DeleteAsync / DeleteAllAsync / the mutating half of CancelAsync call
// repository methods that use EF ExecuteUpdateAsync, which the InMemory
// provider does not support. Those happy paths need a real DB (integration);
// here we cover the read paths + the guard branches that return before the
// bulk update runs (404 + terminal-state 409).
public class WorkflowRunServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowRunService NewSvc(AppDbContext db, FakeUser user)
        => new(new WorkflowRunRepository(db), user, new FakeQueue(), new FakeAudit(),
               NullLogger<WorkflowRunService>.Instance);

    private static Guid SeedRun(AppDbContext db, string status = "running")
    {
        var id = Guid.NewGuid();
        db.Set<WorkflowRunModel>().Add(new WorkflowRunModel
        {
            WorkflowRunId = id,
            WorkflowId = Guid.NewGuid(),
            Status = status,
            Trigger = "manual",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Get_lists_runs()
    {
        using var db = TestDb.NewContext();
        SeedRun(db);

        var ok = Assert.IsType<OkObjectResult>((await NewSvc(db, _caller).GetAsync()).Result);
        Assert.Equal(1, Assert.IsType<ListResponse<WorkflowRunResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task GetById_returns_run()
    {
        using var db = TestDb.NewContext();
        var id = SeedRun(db);
        Assert.IsType<WorkflowRunResponse>((await NewSvc(db, _caller).GetByIdAsync(id)).Value);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>(await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancel_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).CancelAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Cancel_terminal_run_returns_conflict()
    {
        using var db = TestDb.NewContext();
        var id = SeedRun(db, status: "completed");
        Assert.IsType<ConflictObjectResult>((await NewSvc(db, _caller).CancelAsync(id)).Result);
    }
}
