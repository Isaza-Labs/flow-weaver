using System.Text.Json;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Three gates stand between the LLM and any tool: the RBAC role/capability
// check, the autonomy tier (human_only is never agent-executable regardless of
// role), and the per-turn mutation budget that stops runaway agentic loops.
// Every one of them must fail closed — a gate that leaks lets the model act
// beyond what the user authorised.
public class ToolDispatcherTests
{
    private sealed class StubHandler : IToolHandler
    {
        public string Name { get; init; } = "list_workflows";
        public string Description => "stub";
        public JsonElement ParametersSchema => TestJson.Element("""{"type":"object"}""");
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }
        public JsonElement Result { get; set; } = TestJson.Element("""{"ok":true}""");
        public JsonElement? LastArgs { get; private set; }

        public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            LastArgs = args;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedSettings : IAppSettingsService
    {
        private readonly string _mode;
        public FixedSettings(string mode) => _mode = mode;
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = _mode });
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private sealed class StubEffectivePermissions : IEffectivePermissions
    {
        private readonly HashSet<string> _caps;
        public StubEffectivePermissions(params string[] caps) => _caps = new HashSet<string>(caps);
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => Task.FromResult(_caps.Contains(capability));
        public Task<bool> HasAsync(string capability, CancellationToken ct = default)
            => Task.FromResult(_caps.Contains(capability));
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(_caps);
    }

    // Records every trace event so the audit trail for a denial is assertable.
    private sealed class RecordingTrace : flow_weaver_backend.Services.Observability.ITraceLogger
    {
        public List<(string Action, string Status, string? Error)> Events { get; } = new();
        public Task<Guid> StartAsync(string action, string category, object? metadata = null, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());
        public Task CompleteAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task FailAsync(Guid traceEventId, string error, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task EventAsync(string action, string category, string status, object? metadata = null, string? error = null, CancellationToken ct = default)
        {
            Events.Add((action, status, error));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public RecordingTrace Trace { get; } = new();
        public ToolRegistry Registry { get; } = new(NullLogger<ToolRegistry>.Instance);
        public List<StubHandler> Handlers { get; } = new();

        public StubHandler Register(string name)
        {
            var handler = new StubHandler { Name = name };
            Handlers.Add(handler);
            Registry.Register(handler);
            return handler;
        }

        public ToolDispatcher Build(
            string role = "admin",
            string rbacMode = "legacy",
            string[]? capabilities = null,
            IReadOnlyList<string>? roles = null)
        {
            var services = new ServiceCollection();
            foreach (var h in Handlers) services.AddSingleton(h.GetType(), h);
            var sp = services.BuildServiceProvider();

            return new ToolDispatcher(
                Registry,
                sp,
                new FakeUser { Roles = roles ?? new[] { role } },
                Trace,
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
                new StubEffectivePermissions(capabilities ?? Array.Empty<string>()),
                new FixedSettings(rbacMode),
                NullLogger<ToolDispatcher>.Instance);
        }
    }

    private static readonly JsonElement NoArgs = TestJson.Element("{}");

    // ─── unknown tool ───────────────────────────────────────────────────

    [Fact]
    public async Task UnknownTool_IsRefusedAndTraced()
    {
        var f = new Fixture();

        var result = await f.Build().DispatchAsync("no_such_tool", NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("unknown tool", result.ErrorMessage);
        Assert.Contains(f.Trace.Events, e => e.Status == "failed" && e.Action == "tool.call.no_such_tool");
    }

    // The error is surfaced as a JSON result the LLM can read, not just a flag.
    [Fact]
    public async Task ErrorOutputCarriesTheMessageAsJson()
    {
        var f = new Fixture();

        var result = await f.Build().DispatchAsync("no_such_tool", NoArgs, default);

        Assert.Contains("unknown tool", result.Result.GetProperty("error").GetString());
    }

    // A handler message containing quotes/backslashes must not break the JSON
    // envelope — this is why the error result is serialised, not interpolated.
    [Fact]
    public async Task ErrorMessageWithQuotesStaysValidJson()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");
        handler.Throw = new InvalidOperationException("""bad "quoted" \ value""");

        var result = await f.Build().DispatchAsync("create_workflow", NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("""bad "quoted" \ value""", result.Result.GetProperty("error").GetString());
    }

    // ─── RBAC gate (legacy mode) ────────────────────────────────────────

    [Fact]
    public async Task ReadToolIsAllowedForAnyRole()
    {
        var f = new Fixture();
        var handler = f.Register("list_workflows");

        var result = await f.Build(role: "viewer").DispatchAsync("list_workflows", NoArgs, default);

        Assert.True(result.Success);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WriteToolIsDeniedToAViewer()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");

        var result = await f.Build(role: "viewer").DispatchAsync("create_workflow", NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("permission denied", result.ErrorMessage);
        Assert.Equal(0, handler.Calls);   // the handler never ran
    }

    // A denial must be visible in the audit trail, not just returned.
    [Fact]
    public async Task DenialIsTraced()
    {
        var f = new Fixture();
        f.Register("create_workflow");

        await f.Build(role: "viewer").DispatchAsync("create_workflow", NoArgs, default);

        Assert.Contains(f.Trace.Events, e => e.Status == "failed" && e.Error == "permission denied");
    }

    // A multi-role caller is evaluated at their HIGHEST role — otherwise an
    // admin whose claim list starts with "operator" would be under-privileged.
    [Fact]
    public async Task MultiRoleCallerIsEvaluatedAtTheHighestRole()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");

        var result = await f.Build(roles: new[] { "viewer", "admin" })
            .DispatchAsync("create_workflow", NoArgs, default);

        Assert.True(result.Success);
        Assert.Equal(1, handler.Calls);
    }

    // ...but it must not invent privileges the caller doesn't hold.
    [Fact]
    public async Task MultiRoleCallerWithoutAdminStaysCapped()
    {
        var f = new Fixture();
        f.Register("delete_device");

        var result = await f.Build(roles: new[] { "viewer", "operator" })
            .DispatchAsync("delete_device", NoArgs, default);

        Assert.False(result.Success);
    }

    // An unregistered/unknown tool name classifies as dangerous+human_only, so
    // even an admin cannot have the agent run it.
    [Fact]
    public async Task UnclassifiedToolIsTreatedAsHumanOnly()
    {
        var f = new Fixture();
        var handler = f.Register("some_unclassified_tool");

        var result = await f.Build(role: "admin").DispatchAsync("some_unclassified_tool", NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("human_only", result.ErrorMessage);
        Assert.Equal(0, handler.Calls);
    }

    // ─── RBAC gate (granular mode) ──────────────────────────────────────

    // In granular mode the caller must hold the tool's mapped capability;
    // holding a role is not enough on its own.
    [Fact]
    public async Task GranularMode_WithoutTheCapability_IsDenied()
    {
        var f = new Fixture();
        var handler = f.Register("list_workflows");

        var result = await f.Build(role: "admin", rbacMode: RbacModes.Granular, capabilities: Array.Empty<string>())
            .DispatchAsync("list_workflows", NoArgs, default);

        Assert.False(result.Success);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GranularMode_WithTheCapability_IsAllowed()
    {
        var f = new Fixture();
        var handler = f.Register("list_workflows");
        var capability = ToolCapabilityMap.For("list_workflows");
        Assert.NotNull(capability);

        var result = await f.Build(role: "viewer", rbacMode: RbacModes.Granular, capabilities: new[] { capability! })
            .DispatchAsync("list_workflows", NoArgs, default);

        Assert.True(result.Success);
        Assert.Equal(1, handler.Calls);
    }

    // Default-deny: a tool with no capability mapping is refused in granular
    // mode rather than falling through to "allowed".
    [Fact]
    public async Task GranularMode_UnmappedToolIsDefaultDenied()
    {
        var f = new Fixture();
        f.Register("some_unmapped_tool");

        var result = await f.Build(role: "admin", rbacMode: RbacModes.Granular, capabilities: new[] { "anything" })
            .DispatchAsync("some_unmapped_tool", NoArgs, default);

        Assert.False(result.Success);
    }

    // ─── tier gate ──────────────────────────────────────────────────────

    // human_only tools are refused even for an admin who technically has the
    // role — the agent must hand these back to the UI.
    [Fact]
    public async Task HumanOnlyToolIsRefusedEvenForAdmin()
    {
        var f = new Fixture();
        var humanOnly = PermissionClassifier.Matrix
            .First(kv => kv.Value.Tier == PermissionClassifier.TierHumanOnly
                         && kv.Value.Level == "dangerous").Key;
        var handler = f.Register(humanOnly);

        var result = await f.Build(role: "admin").DispatchAsync(humanOnly, NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("human_only", result.ErrorMessage);
        Assert.Contains("from the UI", result.ErrorMessage);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TierBlockIsTraced()
    {
        var f = new Fixture();
        var humanOnly = PermissionClassifier.Matrix
            .First(kv => kv.Value.Tier == PermissionClassifier.TierHumanOnly
                         && kv.Value.Level == "dangerous").Key;
        f.Register(humanOnly);

        await f.Build(role: "admin").DispatchAsync(humanOnly, NoArgs, default);

        Assert.Contains(f.Trace.Events, e => e.Error != null && e.Error.Contains("human_only"));
    }

    // ─── mutation budget ────────────────────────────────────────────────

    // Reads are free, so the agent can keep gathering context all turn without
    // burning the budget.
    [Fact]
    public async Task AutonomousToolsDoNotSpendBudget()
    {
        var f = new Fixture();
        f.Register("list_workflows");
        var dispatcher = f.Build();

        for (var i = 0; i < 50; i++)
            Assert.True((await dispatcher.DispatchAsync("list_workflows", NoArgs, default)).Success);

        Assert.Equal(0, dispatcher.MutationsUsed);
    }

    [Fact]
    public async Task MutatingToolsSpendBudget()
    {
        var f = new Fixture();
        f.Register("create_workflow");
        var dispatcher = f.Build();

        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);
        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);

        Assert.Equal(2, dispatcher.MutationsUsed);
    }

    // The runaway-loop stop: once the budget is spent, further mutations are
    // refused with an instruction to re-authorise.
    [Fact]
    public async Task ExhaustedBudgetRefusesFurtherMutations()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");
        var dispatcher = f.Build();
        dispatcher.MutationBudget = 2;

        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);
        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);
        var blocked = await dispatcher.DispatchAsync("create_workflow", NoArgs, default);

        Assert.False(blocked.Success);
        Assert.Contains("mutation budget exceeded", blocked.ErrorMessage);
        Assert.Contains("re-authorize", blocked.ErrorMessage);
        Assert.Equal(2, handler.Calls);   // the third never reached the handler
    }

    // Reads must keep working after the mutation budget is spent, so the agent
    // can still summarise what it did.
    [Fact]
    public async Task ReadsStillWorkAfterTheBudgetIsExhausted()
    {
        var f = new Fixture();
        f.Register("create_workflow");
        f.Register("list_workflows");
        var dispatcher = f.Build();
        dispatcher.MutationBudget = 1;
        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);
        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);

        Assert.True((await dispatcher.DispatchAsync("list_workflows", NoArgs, default)).Success);
    }

    // A failed handler must not consume a budget slot — the mutation didn't
    // happen, so charging for it would shorten the turn for no reason.
    [Fact]
    public async Task FailedMutationDoesNotSpendBudget()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");
        handler.Throw = new InvalidOperationException("db down");
        var dispatcher = f.Build();

        await dispatcher.DispatchAsync("create_workflow", NoArgs, default);

        Assert.Equal(0, dispatcher.MutationsUsed);
    }

    [Fact]
    public void DefaultBudgetIsTwenty()
    {
        Assert.Equal(20, ToolDispatcher.DefaultMutationBudget);
        Assert.Equal(20, new Fixture().Build().MutationBudget);
    }

    // ─── execution + tracing ────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulCallReturnsTheHandlerResult()
    {
        var f = new Fixture();
        var handler = f.Register("list_workflows");
        handler.Result = TestJson.Element("""{"workflows":[{"id":1}]}""");

        var result = await f.Build().DispatchAsync("list_workflows", NoArgs, default);

        Assert.True(result.Success);
        Assert.Single(result.Result.GetProperty("workflows").EnumerateArray());
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task SuccessfulCallIsTracedAsCompleted()
    {
        var f = new Fixture();
        f.Register("list_workflows");

        await f.Build().DispatchAsync("list_workflows", NoArgs, default);

        var evt = Assert.Single(f.Trace.Events);
        Assert.Equal("tool.call.list_workflows", evt.Action);
        Assert.Equal("completed", evt.Status);
    }

    // A throwing handler is contained: the agent gets an error result it can
    // reason about instead of the turn blowing up.
    [Fact]
    public async Task HandlerExceptionIsContainedAndTraced()
    {
        var f = new Fixture();
        var handler = f.Register("create_workflow");
        handler.Throw = new InvalidOperationException("upstream exploded");

        var result = await f.Build().DispatchAsync("create_workflow", NoArgs, default);

        Assert.False(result.Success);
        Assert.Contains("tool execution failed", result.ErrorMessage);
        Assert.Contains("upstream exploded", result.ErrorMessage);
        Assert.Contains(f.Trace.Events, e => e.Status == "failed");
    }

    // Arguments reach the handler untouched — the dispatcher is a gate, not a
    // transformer.
    [Fact]
    public async Task ArgumentsArePassedThroughUnchanged()
    {
        var f = new Fixture();
        var handler = f.Register("list_workflows");

        var args = TestJson.Element("""{"limit":5,"filter":{"name":"x"}}""");
        await f.Build().DispatchAsync("list_workflows", args, default);

        var seen = Assert.NotNull(handler.LastArgs);
        Assert.Equal(5, seen.GetProperty("limit").GetInt32());
        Assert.Equal("x", seen.GetProperty("filter").GetProperty("name").GetString());
    }
}
