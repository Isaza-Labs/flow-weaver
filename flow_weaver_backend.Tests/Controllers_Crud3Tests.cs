using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AiApiSpec;
using flow_weaver_backend.Services.AiPromptSkill;
using flow_weaver_backend.Services.IntegrationAction;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.StepRun;
using flow_weaver_backend.Services.WorkflowPlan;
using flow_weaver_backend.Services.WorkflowRun;
using flow_weaver_backend.Services.WorkflowTrigger;
using flow_weaver_backend.Services.WorkflowVersion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

public class WorkflowTriggerControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Read_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var svc = new WorkflowTriggerService(new WorkflowTriggerRepository(db), new RepositoryBase<WorkflowModel>(db), _caller, new FakeCrypto(), new FakeAudit(), NullLogger<WorkflowTriggerService>.Instance);
        var c = new WorkflowTriggerController(svc);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
    }
}

public class WorkflowVersionControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Read_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var svc = new WorkflowVersionService(new WorkflowVersionRepository(db), new RepositoryBase<WorkflowModel>(db), _caller, NullLogger<WorkflowVersionService>.Instance);
        var c = new WorkflowVersionController(svc);

        Assert.IsType<NotFoundObjectResult>((await c.GetByWorkflow(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
    }
}

public class WorkflowPlanControllerTests
{
    private readonly FakeUser _caller = new();

    private WorkflowPlanController NewController(AppDbContext db)
        => new(new WorkflowPlanService(new RepositoryBase<WorkflowPlanModel>(db), new SnippetRepository(db),
            new RepositoryBase<WorkflowModel>(db), new UnitOfWork(db), _caller, NullLogger<WorkflowPlanService>.Instance));

    [Fact]
    public async Task Endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var c = NewController(db);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateWorkflowPlan { Intent = "reboot" })).Result);
        var id = ((WorkflowPlanResponse)created.Value!).WorkflowPlanId;
        Assert.IsType<WorkflowPlanResponse>((await c.GetById(id)).Value);
        Assert.IsType<NotFoundObjectResult>((await c.Submit(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>((await c.Build(Guid.NewGuid())).Result);
    }
}

public class RunControllerTests
{
    private readonly FakeUser _caller = new();

    private RunController NewController(AppDbContext db)
        => new(
            new WorkflowRunService(new WorkflowRunRepository(db), _caller, new FakeQueue(), new FakeAudit(), NullLogger<WorkflowRunService>.Instance),
            new StepRunService(new StepRunRepository(db), new RepositoryBase<WorkflowRunModel>(db), _caller, NullLogger<StepRunService>.Instance));

    [Fact]
    public async Task Read_and_guard_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var c = NewController(db);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetSteps(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>((await c.Cancel(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundObjectResult>(await c.Delete(Guid.NewGuid()));
    }
}

public class StepRunControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task GetById_forwards()
    {
        using var db = TestDb.NewContext();
        var svc = new StepRunService(new StepRunRepository(db), new RepositoryBase<WorkflowRunModel>(db), _caller, NullLogger<StepRunService>.Instance);
        var c = new StepRunController(svc);

        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
    }
}

public class ResourcePermissionControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task List_known_type_returns_ok()
    {
        using var db = TestDb.NewContext();
        var svc = new ResourcePermissionService(new ResourcePermissionRepository(db), new UserRepository(db), _caller, new FakeAudit(), NullLogger<ResourcePermissionService>.Instance);
        var c = new ResourcePermissionController(svc);

        var res = await c.List("workflow", Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<OkObjectResult>(res.Result);
    }
}

public class AiPromptSkillControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Crud_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var svc = new AiPromptSkillService(new AiPromptSkillRepository(db), _caller, new FakeSkillPromptLoader(), NullLogger<AiPromptSkillService>.Instance);
        var c = new AiPromptSkillController(svc, null!);   // reseed only used by the /reseed endpoint

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateAiPromptSkill { Name = "a.md", Content = "hi" })).Result);
        var id = ((AiPromptSkillResponse)created.Value!).AiPromptSkillId;
        Assert.IsType<AiPromptSkillResponse>((await c.GetById(id)).Value);
        Assert.IsType<AiPromptSkillResponse>((await c.Delete(id)).Value);
    }
}

public class AiApiSpecControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Crud_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var svc = new AiApiSpecService(new AiApiSpecRepository(db), _caller, new FakeApiSpecIndex(), NullLogger<AiApiSpecService>.Instance);
        var c = new AiApiSpecController(svc, null!);   // reseed only used by the /reseed endpoint

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateAiApiSpec { Api = "netbox", Content = "openapi: 3.0.0\npaths: {}\n" })).Result);
        var id = ((AiApiSpecResponse)created.Value!).AiApiSpecId;
        Assert.IsType<AiApiSpecResponse>((await c.GetById(id)).Value);
        Assert.IsType<AiApiSpecResponse>((await c.Delete(id)).Value);
    }
}

public class IntegrationActionControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Read_endpoints_forward()
    {
        using var db = TestDb.NewContext();
        var svc = new IntegrationActionService(new RepositoryBase<IntegrationActionModel>(db), new IntegrationRepository(db), _caller, NullLogger<IntegrationActionService>.Instance);
        var c = new IntegrationActionController(svc, db, _caller);

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
    }
}
