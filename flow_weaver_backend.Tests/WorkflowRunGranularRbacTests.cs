using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// DEF-006 / TC-FW-046 regression. With rbac_mode=granular, POST /workflow/{id}/run
// must honor a workflow.run grant's environment/resource conditions against the
// CONCRETE workflow being run. Previously the run path never consulted
// IEffectivePermissions (unlike PromotionService for promote), so a grant scoped
// to "environment: [qa]" did NOT stop the holder from launching a draft or
// production workflow — the condition failed open. The check now lives in
// WorkflowController.Run, mirroring PromotionService's env-conditioned gate.
//
// System runs (scheduler / git webhook) call EnqueueRunAsync directly, bypassing
// this endpoint, so they are intentionally unaffected by a user's grant scope.
public class WorkflowRunGranularRbacTests
{
    private static readonly Guid User = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static WorkflowModel SeedWorkflow(AppDbContext db, string environment)
    {
        var wf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Environment = environment,
            Nodes = JsonDocument.Parse("[]").RootElement,
            Edges = JsonDocument.Parse("[]").RootElement,
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        return wf;
    }

    // Scoped workflow.run grant assigned to the test user.
    private static void SeedRunGrant(AppDbContext db, string conditionsJson)
    {
        db.PermissionGrants.Add(new PermissionGrant
        {
            PermissionGrantId = Guid.NewGuid(),
            Name = "custom-runner",
            Enabled = true,
            IsActive = true,
            SubjectIds = new() { User },
            Capabilities = new() { "workflow.run" },
            Conditions = JsonDocument.Parse(conditionsJson).RootElement.Clone(),
        });
        db.SaveChanges();
    }

    // The controller re-reads the run row after enqueue to build its 202 body.
    private static void SeedRunRow(AppDbContext db, Guid workflowId, Guid runId)
    {
        db.WorkflowRuns.Add(new WorkflowRunModel
        {
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            Status = "pending",
            Trigger = "manual",
            IsActive = true,
        });
        db.SaveChanges();
    }

    private static WorkflowController NewController(
        AppDbContext db, string rbacMode, IWorkflowExecutor executor, params string[] roles)
    {
        var caller = new FakeUser
        {
            UserId = User,
            Roles = roles.Length > 0 ? roles : new[] { "viewer" },
        };
        var effective = new EffectivePermissions(caller, new PermissionGrantReader(db));
        return new WorkflowController(
            service: null!,
            triggers: null!,
            executor: executor,
            promotion: null!,
            caller: caller,
            db: db,
            versions: null!,
            exportService: null!,
            // Not exercised by these RBAC cases — the run path never touches
            // export or bundle import.
            bundleService: null!,
            bundleImporter: null!,
            snippets: null!,
            effective: effective,
            settings: new StubSettings(rbacMode),
            logger: NullLogger<WorkflowController>.Instance);
    }

    [Fact]
    public async Task Granular_env_scoped_runner_is_blocked_403_running_another_environment()
    {
        // Grant: workflow.run scoped to qa. Target: a DRAFT workflow.
        using var db = NewDb(nameof(Granular_env_scoped_runner_is_blocked_403_running_another_environment));
        var wf = SeedWorkflow(db, "draft");
        SeedRunGrant(db, """{ "environment": ["qa"] }""");
        var executor = new RecordingExecutor();
        var controller = NewController(db, RbacModes.Granular, executor);

        var result = await controller.Run(wf.WorkflowId, new RunWorkflowRequest(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, obj.StatusCode);
        Assert.False(executor.Called);   // blocked BEFORE any run row is enqueued
    }

    [Fact]
    public async Task Granular_env_scoped_runner_can_run_in_the_granted_environment()
    {
        using var db = NewDb(nameof(Granular_env_scoped_runner_can_run_in_the_granted_environment));
        var wf = SeedWorkflow(db, "qa");
        SeedRunGrant(db, """{ "environment": ["qa"] }""");
        var executor = new RecordingExecutor();
        SeedRunRow(db, wf.WorkflowId, executor.RunId);
        var controller = NewController(db, RbacModes.Granular, executor);

        var result = await controller.Run(wf.WorkflowId, new RunWorkflowRequest(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(202, obj.StatusCode);
        Assert.True(executor.Called);
    }

    [Fact]
    public async Task Granular_resource_scoped_runner_cannot_run_a_different_workflow()
    {
        // Grant scoped to ONE workflow; the user runs a DIFFERENT one (same env).
        using var db = NewDb(nameof(Granular_resource_scoped_runner_cannot_run_a_different_workflow));
        var granted = SeedWorkflow(db, "qa");
        var other = SeedWorkflow(db, "qa");
        SeedRunGrant(db, $$"""{ "resource": { "type": "workflow", "id": "{{granted.WorkflowId}}" } }""");
        var executor = new RecordingExecutor();
        var controller = NewController(db, RbacModes.Granular, executor);

        var result = await controller.Run(other.WorkflowId, new RunWorkflowRequest(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, obj.StatusCode);
        Assert.False(executor.Called);
    }

    [Fact]
    public async Task Granular_admin_bypasses_the_run_condition()
    {
        // Admin holds no grant but bypasses every conditioned check.
        using var db = NewDb(nameof(Granular_admin_bypasses_the_run_condition));
        var wf = SeedWorkflow(db, "production");
        var executor = new RecordingExecutor();
        SeedRunRow(db, wf.WorkflowId, executor.RunId);
        var controller = NewController(db, RbacModes.Granular, executor, roles: "admin");

        var result = await controller.Run(wf.WorkflowId, new RunWorkflowRequest(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(202, obj.StatusCode);
        Assert.True(executor.Called);
    }

    [Fact]
    public async Task Legacy_mode_skips_the_granular_run_check()
    {
        // In legacy mode the coarse [HasPermission] attribute (pipeline-level)
        // handles auth; the action must NOT run the conditioned block, so a user
        // with no grant at all still reaches the executor here.
        using var db = NewDb(nameof(Legacy_mode_skips_the_granular_run_check));
        var wf = SeedWorkflow(db, "draft");
        var executor = new RecordingExecutor();
        SeedRunRow(db, wf.WorkflowId, executor.RunId);
        var controller = NewController(db, RbacModes.Legacy, executor);

        var result = await controller.Run(wf.WorkflowId, new RunWorkflowRequest(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(202, obj.StatusCode);
        Assert.True(executor.Called);
    }

    // Records whether the run was enqueued and returns a stable id the test
    // seeds a matching run row for. ExecuteRunAsync is never hit on this path.
    private sealed class RecordingExecutor : IWorkflowExecutor
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public bool Called { get; private set; }

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId,
            RunWorkflowRequest request, CancellationToken ct, string trigger = "manual")
        {
            Called = true;
            return Task.FromResult(RunId);
        }

        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotImplementedException();
    }

    private sealed class StubSettings : IAppSettingsService
    {
        private readonly string _mode;
        public StubSettings(string mode) => _mode = mode;

        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = _mode });

        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }
}
