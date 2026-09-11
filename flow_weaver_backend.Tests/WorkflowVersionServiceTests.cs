using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowVersion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowVersionModel = flow_weaver_backend.Models.WorkflowVersion;

namespace flow_weaver_backend.Tests;

public class WorkflowVersionServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowVersionService NewSvc(AppDbContext db, FakeUser user)
        => new(new WorkflowVersionRepository(db), new RepositoryBase<WorkflowModel>(db), user,
               NullLogger<WorkflowVersionService>.Instance);

    private static Guid SeedWorkflow(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Set<WorkflowModel>().Add(new WorkflowModel
        {
            WorkflowId = id,
            Name = "wf",
            Version = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedVersion(AppDbContext db, Guid workflowId, int version = 1)
    {
        var id = Guid.NewGuid();
        db.Set<WorkflowVersionModel>().Add(new WorkflowVersionModel
        {
            WorkflowVersionId = id,
            WorkflowId = workflowId,
            Version = version,
            PromotedBy = "tester",
            PromotedAt = DateTime.UtcNow,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task GetByWorkflow_404_when_workflow_missing()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).GetByWorkflowAsync(Guid.NewGuid());
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task GetByWorkflow_lists_versions()
    {
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db);
        SeedVersion(db, wf, 1);
        SeedVersion(db, wf, 2);

        var ok = Assert.IsType<OkObjectResult>((await NewSvc(db, _caller).GetByWorkflowAsync(wf)).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<WorkflowVersionResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task GetById_returns_version()
    {
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db);
        var vid = SeedVersion(db, wf);

        Assert.IsType<WorkflowVersionResponse>((await NewSvc(db, _caller).GetByIdAsync(vid)).Value);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }
}
