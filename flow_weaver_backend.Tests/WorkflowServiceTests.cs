using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Validation;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// WorkflowService has a large collaborator graph (schema/reference/vendor
// validators, policy, permissions, app settings). Read + guard paths are
// covered here with a real schema validator + "allow/ok" fakes for the rest;
// the Post/Update happy paths (which drive those collaborators) are integration
// territory — a follow-up phase can layer them on the same fakes.
public class WorkflowServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowService NewSvc(AppDbContext db, FakeUser user)
        => new(
            new RepositoryBase<WorkflowModel>(db),
            user,
            new FakeSchemaValidator(),
            new FakeReferenceValidator(),
            new FakeVendorCommandValidator(),
            new FakeAudit(),
            new FakeTrace(),
            new FakePolicyEvaluator(),
            new ResourcePermissionService(new ResourcePermissionRepository(db), new UserRepository(db),
                user, new FakeAudit(), NullLogger<ResourcePermissionService>.Instance),
            new FakeAppSettings(),
            NullLogger<WorkflowService>.Instance);

    private static Guid SeedWorkflow(AppDbContext db, string name = "wf")
    {
        var id = Guid.NewGuid();
        db.Set<WorkflowModel>().Add(new WorkflowModel
        {
            WorkflowId = id,
            Name = name,
            Version = 1,
            Nodes = TestJson.Element("[]"),
            Edges = TestJson.Element("[]"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Get_lists_workflows()
    {
        using var db = TestDb.NewContext();
        SeedWorkflow(db, "a");
        SeedWorkflow(db, "b");

        var ok = Assert.IsType<OkObjectResult>((await NewSvc(db, _caller).GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<WorkflowResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task GetById_returns_workflow()
    {
        using var db = TestDb.NewContext();
        var id = SeedWorkflow(db);
        Assert.IsType<WorkflowResponse>((await NewSvc(db, _caller).GetByIdAsync(id)).Value);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateWorkflow())).Result);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid())).Result);
    }
}
