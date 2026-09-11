using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;


public class WorkflowProductionImmutabilityTests
{
    private static readonly FakeUser Caller = new();

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static WorkflowModel SeedWorkflow(AppDbContext db, string environment)
    {
        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Version = 3,
            Environment = environment,
            Nodes = JsonDocument.Parse("[]").RootElement,
            Edges = JsonDocument.Parse("[]").RootElement,
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf;
    }

    // Only the deps reached before/at the 409 (and on the delete path) are
    // real; the mutation-path deps are never hit here and stay null.
    private static WorkflowService NewService(AppDbContext db) =>
        new(
            new RepositoryBase<WorkflowModel>(db),
            Caller,
            schemaValidator: null!,
            referenceValidator: null!,
            vendorCommandValidator: null!,
            audit: new NoOpAuditLogger(),
            trace: new NoOpTraceLogger(),
            policies: null!,
            permissions: null!,
            appSettings: new GatingDisabledAppSettings(),
            logger: NullLogger<WorkflowService>.Instance);

    [Fact]
    public async Task Updating_a_production_workflow_is_blocked_with_409()
    {
        using var db = NewDb(nameof(Updating_a_production_workflow_is_blocked_with_409));
        var wf = SeedWorkflow(db, "production");
        var svc = NewService(db);

        var result = await svc.UpdateAsync(wf.WorkflowId, new UpdateWorkflow { Name = "hacked" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, obj.StatusCode);
    }

    [Fact]
    public async Task Updating_a_missing_workflow_is_404()
    {
        using var db = NewDb(nameof(Updating_a_missing_workflow_is_404));
        var svc = NewService(db);

        var result = await svc.UpdateAsync(Guid.NewGuid(), new UpdateWorkflow { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Deleting_a_production_workflow_is_blocked_with_409()
    {
        // FR-019: a soft-delete mutates the production artifact, so DeleteAsync
        // must refuse it with the same 409 production_immutable as UpdateAsync.
        using var db = NewDb(nameof(Deleting_a_production_workflow_is_blocked_with_409));
        var wf = SeedWorkflow(db, "production");
        var svc = NewService(db);

        var result = await svc.DeleteAsync(wf.WorkflowId);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, obj.StatusCode);

        // The artifact is untouched — still active in the database.
        var stored = await db.Workflows.FindAsync(wf.WorkflowId);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task Deleting_a_draft_workflow_is_allowed()
    {
        // Guard is scoped to production: a draft still soft-deletes cleanly.
        using var db = NewDb(nameof(Deleting_a_draft_workflow_is_allowed));
        var wf = SeedWorkflow(db, "draft");
        var svc = NewService(db);

        var result = await svc.DeleteAsync(wf.WorkflowId);

        Assert.False(result.Result is ObjectResult { StatusCode: 409 });
        var stored = await db.Workflows.FindAsync(wf.WorkflowId);
        Assert.False(stored!.IsActive);
    }

    [Fact]
    public async Task Deleting_a_missing_workflow_is_404()
    {
        using var db = NewDb(nameof(Deleting_a_missing_workflow_is_404));
        var svc = NewService(db);

        var result = await svc.DeleteAsync(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }
}

// Feature-flag gate disabled → AuthorizeAsync short-circuits to "allowed"
// without consulting IResourcePermissionService.
internal sealed class GatingDisabledAppSettings : IAppSettingsService
{
    public Task<AppSettings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(AppSettings.Default);

    public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
        => Task.FromResult(updated);
}
