using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.WorkflowPlan;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Tests;

public class WorkflowPlanServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowPlanService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<WorkflowPlanModel>(db), new SnippetRepository(db),
               new RepositoryBase<WorkflowModel>(db), new UnitOfWork(db), user,
               NullLogger<WorkflowPlanService>.Instance);

    private static CreateWorkflowPlan Sample(string intent = "reboot the edge routers") => new() { Intent = intent };

    private static Guid CreatedId(ActionResult<WorkflowPlanResponse> r)
        => ((WorkflowPlanResponse)((CreatedAtActionResult)r.Result!).Value!).WorkflowPlanId;

    [Fact]
    public async Task Post_persists()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_intent()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateWorkflowPlan { Intent = "" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
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
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample("a"));
        await svc.PostAsync(Sample("b"));
        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<WorkflowPlanResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Submit_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).SubmitForApprovalAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Approve_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).ApproveAsync(Guid.NewGuid(), new ApprovePlan { ApprovedBy = "admin" });
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Reject_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).RejectAsync(Guid.NewGuid(), new RejectPlan { ApprovedBy = "admin", Reason = "no" });
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Build_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).BuildAsync(Guid.NewGuid())).Result);
    }
}
