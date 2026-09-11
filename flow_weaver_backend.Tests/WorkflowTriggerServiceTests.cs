using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

public class WorkflowTriggerServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowTriggerService NewSvc(AppDbContext db, FakeUser user)
        => new(new WorkflowTriggerRepository(db), new RepositoryBase<WorkflowModel>(db), user,
               new FakeCrypto(), new FakeAudit(), NullLogger<WorkflowTriggerService>.Instance);

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

    private static CreateWorkflowTrigger Webhook(string name = "hook")
        => new() { Name = name, Type = "webhook" };

    private static Guid CreatedId(ActionResult<WorkflowTriggerResponse> r)
        => ((WorkflowTriggerResponse)((CreatedAtActionResult)r.Result!).Value!).WorkflowTriggerId;

    [Fact]
    public async Task PostForWorkflow_persists_webhook()
    {
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db);

        var result = await NewSvc(db, _caller).PostForWorkflowAsync(wf, Webhook());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.IsType<WorkflowTriggerResponse>(created.Value);
    }

    [Fact]
    public async Task PostForWorkflow_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForWorkflowAsync(Guid.NewGuid(), new CreateWorkflowTrigger { Name = "", Type = "webhook" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task PostForWorkflow_rejects_blank_type()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForWorkflowAsync(Guid.NewGuid(), new CreateWorkflowTrigger { Name = "n", Type = "" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task PostForWorkflow_rejects_schedule_without_cron()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForWorkflowAsync(Guid.NewGuid(), new CreateWorkflowTrigger { Name = "n", Type = "schedule" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task PostForWorkflow_404_when_workflow_missing()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForWorkflowAsync(Guid.NewGuid(), Webhook());
        Assert.IsType<NotFoundObjectResult>(r.Result);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Get_lists_rows()
    {
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db);
        var svc = NewSvc(db, _caller);
        await svc.PostForWorkflowAsync(wf, Webhook("a"));
        await svc.PostForWorkflowAsync(wf, Webhook("b"));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<WorkflowTriggerResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateWorkflowTrigger())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var wf = SeedWorkflow(db);
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostForWorkflowAsync(wf, Webhook()));

        await svc.DeleteAsync(id);

        Assert.False(db.Set<flow_weaver_backend.Models.WorkflowTrigger>().Single().IsActive);
    }
}
