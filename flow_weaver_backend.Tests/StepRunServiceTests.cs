using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.StepRun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

public class StepRunServiceTests
{
    private readonly FakeUser _caller = new();

    private static StepRunService NewSvc(AppDbContext db, FakeUser user)
        => new(new StepRunRepository(db), new RepositoryBase<WorkflowRunModel>(db), user,
               NullLogger<StepRunService>.Instance);

    private static Guid SeedRun(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Set<WorkflowRunModel>().Add(new WorkflowRunModel
        {
            WorkflowRunId = id,
            WorkflowId = Guid.NewGuid(),
            Status = "running",
            Trigger = "manual",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedStep(AppDbContext db, Guid runId)
    {
        var id = Guid.NewGuid();
        db.Set<StepRunModel>().Add(new StepRunModel
        {
            StepRunId = id,
            WorkflowRunId = runId,
            NodeId = "n1",
            Status = "completed",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task GetByRun_404_when_run_missing()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByRunAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task GetByRun_lists_steps()
    {
        using var db = TestDb.NewContext();
        var run = SeedRun(db);
        SeedStep(db, run);
        SeedStep(db, run);

        var ok = Assert.IsType<OkObjectResult>((await NewSvc(db, _caller).GetByRunAsync(run)).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<StepRunResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task GetById_returns_step()
    {
        using var db = TestDb.NewContext();
        var run = SeedRun(db);
        var step = SeedStep(db, run);

        Assert.IsType<StepRunResponse>((await NewSvc(db, _caller).GetByIdAsync(step)).Value);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }
}
