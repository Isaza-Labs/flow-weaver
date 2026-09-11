using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Credential;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Snippet;
using flow_weaver_backend.Services.VendorCommand;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PolicyModel = flow_weaver_backend.Models.Policy;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Tests;

// Drives the get/create/action AI tool handlers through ExecuteAsync. None use
// a hard GetProperty, so missing args yield an error/not-found JSON object
// (never a throw). Args below aim at happy paths where cheap, else the
// not-found branch. Every handler must return a JSON object/array.
public class AiActionHandlerTests
{
    private readonly FakeUser _caller = new();
    private static JsonElement A(object o) => JsonSerializer.SerializeToElement(o);
    private static void Ok(JsonElement e) => Assert.True(e.ValueKind is JsonValueKind.Object or JsonValueKind.Array, $"got {e.ValueKind}");

    private ILogger<T> Log<T>() => NullLogger<T>.Instance;

    [Fact]
    public async Task get_run_details()
    {
        using var db = TestDb.NewContext();
        var h = new GetRunDetailsHandler(new WorkflowRunRepository(db), new StepRunRepository(db), _caller, Log<GetRunDetailsHandler>());
        Ok(await h.ExecuteAsync(A(new { run_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task get_step_logs()
    {
        using var db = TestDb.NewContext();
        var h = new GetStepLogsHandler(new StepRunRepository(db), _caller, Log<GetStepLogsHandler>());
        Ok(await h.ExecuteAsync(A(new { step_run_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task get_workflow_details()
    {
        using var db = TestDb.NewContext();
        var h = new GetWorkflowDetailsHandler(new RepositoryBase<WorkflowModel>(db), _caller, TestAppLinks.Relative(), Log<GetWorkflowDetailsHandler>());
        Ok(await h.ExecuteAsync(A(new { workflow_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task operation_detail_and_discover()
    {
        using var db = TestDb.NewContext();
        var od = new OperationDetailHandler(new AiApiSpecRepository(db), new FakeApiSpecIndex(), _caller, Log<OperationDetailHandler>());
        Ok(await od.ExecuteAsync(A(new { operation_id = "getThing" }), CancellationToken.None));
        var disc = new DiscoverOperationsHandler(new AiApiSpecRepository(db), new FakeApiSpecIndex(), _caller, Log<DiscoverOperationsHandler>());
        Ok(await disc.ExecuteAsync(A(new { limit = 10 }), CancellationToken.None));
    }

    [Fact]
    public async Task validate_ssh_commands()
    {
        var h = new ValidateSshCommandsHandler(_caller, new FakeVendorCommandValidator(), new FakeVendorCommandRegistry(), Log<ValidateSshCommandsHandler>());
        Ok(await h.ExecuteAsync(A(new { device_type = "cisco_ios", commands = new[] { "show version" } }), CancellationToken.None));
    }

    [Fact]
    public async Task create_policy()
    {
        using var db = TestDb.NewContext();
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), Log<PolicyService>());
        var h = new CreatePolicyHandler(svc, Log<CreatePolicyHandler>());
        Ok(await h.ExecuteAsync(A(new { name = "no-prod", rule = new { action = "deny" } }), CancellationToken.None));
    }

    [Fact]
    public async Task create_snippet()
    {
        using var db = TestDb.NewContext();
        var svc = new SnippetService(new SnippetRepository(db), new StepRunRepository(db), _caller, new FakeAudit(), new FakeTrace(), Log<SnippetService>());
        var h = new CreateSnippetHandler(svc, Log<CreateSnippetHandler>());
        Ok(await h.ExecuteAsync(A(new { name = "s", type = "rest_call", target_mode = "per_device" }), CancellationToken.None));
    }

    [Fact]
    public async Task create_and_delete_vendor_command()
    {
        using var db = TestDb.NewContext();
        var svc = new VendorCommandService(new VendorCommandRepository(db), _caller, new FakeAudit(), new FakeTrace(), new FakeVendorCommandRegistry(), Log<VendorCommandService>());
        var create = new CreateVendorCommandHandler(svc, Log<CreateVendorCommandHandler>());
        Ok(await create.ExecuteAsync(A(new { device_type = "cisco_ios", value = "show version" }), CancellationToken.None));

        var del = new DeleteVendorCommandHandler(svc, Log<DeleteVendorCommandHandler>());
        Ok(await del.ExecuteAsync(A(new { vendor_command_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task create_workflow_plan()
    {
        using var db = TestDb.NewContext();
        var h = new CreateWorkflowPlanHandler(new RepositoryBase<WorkflowPlanModel>(db), new PlanFeatureRepository(db), _caller, Log<CreateWorkflowPlanHandler>());
        Ok(await h.ExecuteAsync(A(new { intent = "reboot the edge routers" }), CancellationToken.None));
    }

    [Fact]
    public async Task evaluate_prompt_sufficiency()
    {
        using var db = TestDb.NewContext();
        var h = new EvaluatePromptSufficiencyHandler(new SnippetRepository(db), new WorkflowRepository(db), _caller, Log<EvaluatePromptSufficiencyHandler>());
        Ok(await h.ExecuteAsync(A(new { prompt = "reboot all cisco routers in site A" }), CancellationToken.None));
    }

    [Fact]
    public async Task mark_workflow_ready()
    {
        using var db = TestDb.NewContext();
        var h = new MarkWorkflowReadyHandler(new RepositoryBase<WorkflowModel>(db), new SimulationResultRepository(db), _caller, Log<MarkWorkflowReadyHandler>());
        Ok(await h.ExecuteAsync(A(new { workflow_id = Guid.NewGuid() }), CancellationToken.None));
    }

    [Fact]
    public async Task update_workflow_node_config()
    {
        using var db = TestDb.NewContext();
        var h = new UpdateWorkflowNodeConfigHandler(new RepositoryBase<WorkflowModel>(db), _caller, new FakeAudit(), new FakeReferenceValidator(), Log<UpdateWorkflowNodeConfigHandler>());
        Ok(await h.ExecuteAsync(A(new { workflow_id = Guid.NewGuid(), node_id = "n1", config_overrides = new { } }), CancellationToken.None));
    }

    [Fact]
    public async Task set_user_role_missing_user()
    {
        using var db = TestDb.NewContext();
        var h = new SetUserRoleHandler(
            db,
            _caller,
            new NoOpBuiltinGrantSync(),
            Log<SetUserRoleHandler>()
            );
        Ok(await h.ExecuteAsync(A(new { user_id = Guid.NewGuid(), role = "operator" }), CancellationToken.None));
    }

    [Fact]
    public async Task grant_resource_permission()
    {
        using var db = TestDb.NewContext();
        var userId = Guid.NewGuid();
        db.Set<User>().Add(new User { UserId = userId, Username = "g", Email = "g@x.com", PasswordHash = "x", Role = "editor", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var svc = new ResourcePermissionService(new ResourcePermissionRepository(db), new UserRepository(db), _caller, new FakeAudit(), Log<ResourcePermissionService>());
        var h = new GrantResourcePermissionHandler(svc, Log<GrantResourcePermissionHandler>());
        Ok(await h.ExecuteAsync(A(new { resource_type = "workflow", resource_id = Guid.NewGuid(), subject_type = "user", subject_id = userId, role = "editor" }), CancellationToken.None));
    }

    [Fact]
    public async Task create_user()
    {
        using var db = TestDb.NewContext();
        var h = new CreateUserHandler(db, new PasswordHasher<User>(),
            new PasswordPolicy(Options.Create(new AuthOptions()), Log<PasswordPolicy>()), _caller,
            new NoOpBuiltinGrantSync(), Log<CreateUserHandler>());
        Ok(await h.ExecuteAsync(A(new { username = "newbie", email = "newbie@example.com", password = "Str0ng!Passw0rd1", role = "operator" }), CancellationToken.None));
    }

    [Fact]
    public async Task update_vendor_command_missing()
    {
        using var db = TestDb.NewContext();
        var svc = new VendorCommandService(new VendorCommandRepository(db), _caller, new FakeAudit(), new FakeTrace(), new FakeVendorCommandRegistry(), Log<VendorCommandService>());
        var h = new UpdateVendorCommandHandler(svc, Log<UpdateVendorCommandHandler>());
        Ok(await h.ExecuteAsync(A(new { vendor_command_id = Guid.NewGuid(), value = "show clock" }), CancellationToken.None));
    }

    [Fact]
    public async Task run_acceptance_tests_missing_workflow()
    {
        using var db = TestDb.NewContext();
        var h = new RunAcceptanceTestsHandler(new RepositoryBase<WorkflowModel>(db), new WorkflowAcceptanceTestRepository(db),
            new WorkflowRunRepository(db), new StepRunRepository(db), _caller, Log<RunAcceptanceTestsHandler>());
        Ok(await h.ExecuteAsync(A(new { workflow_id = Guid.NewGuid() }), CancellationToken.None));
    }
}
