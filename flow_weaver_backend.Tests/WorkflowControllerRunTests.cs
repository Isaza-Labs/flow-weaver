using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// POST /workflow/{id}/run. The [HasPermission] attribute above the action is
// COARSE — it only proves the caller holds workflow.run somewhere. A grant
// scoped to an environment ("run in qa only") has to be re-checked against the
// CONCRETE workflow, or the scope silently fails open and a qa-only runner can
// launch production. That second check is what this suite pins.
public class WorkflowControllerRunTests
{

    private sealed class RecordingExecutor : IWorkflowExecutor
    {
        public List<(Guid WorkflowId, string Trigger)> Enqueued { get; } = new();
        public Guid RunId { get; set; } = Guid.NewGuid();
        public Exception? Throw { get; set; }
        public bool SkipRunRow { get; set; }
        public AppDbContext? Db { get; set; }

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId, RunWorkflowRequest request,
            CancellationToken ct, string trigger = "manual")
        {
            if (Throw is not null) throw Throw;
            Enqueued.Add((workflowId, trigger));
            if (!SkipRunRow && Db is not null)
            {
                Db.WorkflowRuns.Add(new flow_weaver_backend.Models.WorkflowRun
                {
                    WorkflowRunId = RunId,
                    WorkflowId = workflowId,
                    Status = "pending",
                    InputPayload = request.Input,
                    Trigger = trigger,
                    IsActive = true,
                });
                Db.SaveChanges();
            }
            return Task.FromResult(RunId);
        }

        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotSupportedException();
    }

    // Answers the environment-scoped capability check.
    private sealed class ScopedPermissions : IEffectivePermissions
    {
        public string? AllowedEnvironment { get; set; }   // null = allow everything
        public List<PermissionContext> Checked { get; } = new();

        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
        {
            Checked.Add(ctx);
            return Task.FromResult(AllowedEnvironment is null
                || string.Equals(ctx.Environment, AllowedEnvironment, StringComparison.OrdinalIgnoreCase));
        }
        public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
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

    // The controller only forwards to these; a call means "not the path under
    // test", so they fail loudly.
    private sealed class UnusedWorkflowService : IWorkflow
    {
        public Task<ActionResult<ListResponse<WorkflowResponse>>> GetAsync(int limit = 50, int offset = 0)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> PostAsync(CreateWorkflow dto) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> UpdateAsync(Guid id, UpdateWorkflow dto) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class UnusedTriggerService : IWorkflowTrigger
    {
        public Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetAsync(int limit = 50, int offset = 0)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> PostAsync(CreateWorkflowTrigger dto) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> UpdateAsync(Guid id, UpdateWorkflowTrigger dto) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetByWorkflowAsync(Guid workflowId, int limit = 50, int offset = 0)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> PostForWorkflowAsync(Guid workflowId, CreateWorkflowTrigger dto)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> RotateWebhookSecretAsync(Guid id)
            => throw new NotSupportedException();
    }

    private sealed class UnusedPromotion : IPromotionService
    {
        public Task<ActionResult<WorkflowResponse>> PromoteAsync(Guid id, PromoteRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> RollbackAsync(Guid id, int toVersion, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<DiffResult>> DiffAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> CloneAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UnusedVersions : IWorkflowVersion
    {
        public Task<ActionResult<ListResponse<WorkflowVersionResponse>>> GetByWorkflowAsync(
            Guid workflowId, int limit = 50, int offset = 0) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowVersionResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class StubExportService : IWorkflowExportService
    {
        public ExportPayload Payload { get; set; } = new("name: wf", "application/x-yaml", "wf.yaml");
        public string? FormatSeen { get; private set; }
        public Task<ExportPayload> ExportAsync(Guid workflowId, string format, CancellationToken ct)
        {
            FormatSeen = format;
            return Task.FromResult(Payload);
        }

        // Import is a different endpoint; a call here means the wrong path ran.
        public ImportPayload ParseImport(string raw, string format) => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingExecutor Executor { get; } = new();
        public ScopedPermissions Permissions { get; } = new();
        public StubExportService Export { get; } = new();

        public WorkflowController Build(string rbacMode = "legacy")
        {
            Executor.Db = Db;
            return new WorkflowController(
                new UnusedWorkflowService(),
                new UnusedTriggerService(),
                Executor,
                new UnusedPromotion(),
                new FakeUser(),
                Db,
                new UnusedVersions(),
                Export,
                BundleTest.Svc(Db),
                new flow_weaver_backend.Services.Workflow.WorkflowBundleImporter(
                    // Constructed, never invoked: these tests exercise the YAML
                    // import path, which never reaches the bundle importer.
                    BundleTest.Svc(Db), workflows: null!, triggers: null!,
                    new SnippetRepository(Db), new UnitOfWork(Db),
                    NullLogger<flow_weaver_backend.Services.Workflow.WorkflowBundleImporter>.Instance),
                new SnippetRepository(Db),
                Permissions,
                new FixedSettings(rbacMode),
                NullLogger<WorkflowController>.Instance)
            {
                ControllerContext = TestCtx.WithUser(),
            };
        }

        public Guid SeedWorkflow(
            string environment = "draft", string metadata = "{}",
            string name = "wf", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = name,
                Environment = environment,
                Version = 1,
                SchemaVersion = "v1",
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                Metadata = TestJson.Element(metadata),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static RunWorkflowRequest Run() => new() { Input = TestJson.Element("{}") };

    // The controller answers errors through Problems.*, i.e. a ProblemDetails
    // payload — read its Detail rather than ToString()ing the wrapper.
    private static string DetailOf(ObjectResult result)
        => Assert.IsAssignableFrom<ProblemDetails>(result.Value).Detail ?? string.Empty;

    // ─── legacy mode: no second check ───────────────────────────────────

    // In legacy mode the coarse attribute is the whole gate, so the
    // environment-scoped check must not run at all.
    [Fact]
    public async Task LegacyModeSkipsTheScopedCheck()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production");

        var result = await f.Build(rbacMode: "legacy").Run(id, Run(), default);

        Assert.Empty(f.Permissions.Checked);
        Assert.Equal(202, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ─── granular mode: the scoped re-check ─────────────────────────────

    // The bug this prevents: a grant scoped to qa silently letting a
    // production run through.
    [Fact]
    public async Task GranularModeRefusesAnEnvironmentTheGrantDoesNotCover()
    {
        using var f = new Fixture();
        f.Permissions.AllowedEnvironment = "qa";
        var id = f.SeedWorkflow(environment: "production");

        var result = await f.Build(rbacMode: RbacModes.Granular).Run(id, Run(), default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(f.Executor.Enqueued);
    }

    [Fact]
    public async Task GranularModeAllowsTheCoveredEnvironment()
    {
        using var f = new Fixture();
        f.Permissions.AllowedEnvironment = "qa";
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build(rbacMode: RbacModes.Granular).Run(id, Run(), default);

        Assert.Equal(202, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Single(f.Executor.Enqueued);
    }

    // The check must be against the CONCRETE workflow, not a generic context —
    // otherwise a resource-scoped grant could not be honoured either.
    [Fact]
    public async Task TheCheckCarriesTheWorkflowAsItsResource()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build(rbacMode: RbacModes.Granular).Run(id, Run(), default);

        var ctx = Assert.Single(f.Permissions.Checked);
        Assert.Equal("qa", ctx.Environment);
        Assert.Equal("workflow", ctx.ResourceType);
        Assert.Equal(id, ctx.ResourceId);
    }

    // A missing workflow is not "unauthorized" — it must fall through so the
    // executor reports not-found in its usual shape, identically in both modes.
    [Fact]
    public async Task AMissingWorkflowFallsThroughToTheExecutor()
    {
        using var f = new Fixture();
        f.Permissions.AllowedEnvironment = "nothing-matches";
        f.Executor.Throw = new WorkflowExecutorException("workflow not found");

        var result = await f.Build(rbacMode: RbacModes.Granular).Run(Guid.NewGuid(), Run(), default);

        Assert.Empty(f.Permissions.Checked);
        Assert.Equal(409, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ─── dispatch + failure shapes ──────────────────────────────────────

    [Fact]
    public async Task ASuccessfulRunReturnsTheCreatedRun()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await f.Build().Run(id, Run(), default);

        var accepted = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(202, accepted.StatusCode);
        var body = Assert.IsType<WorkflowRunResponse>(accepted.Value);
        Assert.Equal(f.Executor.RunId, body.Id);
        Assert.Equal(id, body.WorkflowId);
        Assert.Equal("pending", body.Status);
    }

    // Prerequisites that aren't met (missing credentials, unresolved snippets)
    // are a 409: the row exists, the operation just can't proceed. A 404 read
    // as "the workflow is gone", which was misleading.
    [Fact]
    public async Task UnmetPrerequisitesAre409NotFound()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Executor.Throw = new WorkflowExecutorException("integration still in needs_config");

        var result = await f.Build().Run(id, Run(), default);

        var conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Contains("needs_config", DetailOf(conflict));
    }

    // A structurally broken DAG is the caller's mistake — 400, with the reason.
    [Fact]
    public async Task ABrokenDagIs400()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Executor.Throw = new DagParseException("cycle detected: a → b → a");

        var result = await f.Build().Run(id, Run(), default);

        var bad = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, bad.StatusCode);
        Assert.Contains("cycle detected", DetailOf(bad));
    }

    // A run that is enqueued but then unreadable is an internal fault, not a
    // client error.
    [Fact]
    public async Task ARunThatVanishesAfterEnqueueIs500()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.Executor.SkipRunRow = true;

        var result = await f.Build().Run(id, Run(), default);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ─── ListSubflows ───────────────────────────────────────────────────

    private static List<WorkflowResponse> Subflows(ActionResult<IEnumerable<WorkflowResponse>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IEnumerable<WorkflowResponse>>(ok.Value).ToList();
    }

    // Only workflows explicitly tagged appear in the subflow picker.
    [Fact]
    public async Task OnlyTaggedWorkflowsAreListedAsSubflows()
    {
        using var f = new Fixture();
        f.SeedWorkflow(name: "tagged", metadata: """{"is_subflow":true}""");
        f.SeedWorkflow(name: "plain", metadata: "{}");

        var rows = Subflows(await f.Build().ListSubflows(ct: default));

        Assert.Equal("tagged", Assert.Single(rows).Name);
    }

    // The flag must be a real boolean true — a truthy string is not a tag.
    [Theory]
    [InlineData("""{"is_subflow":false}""")]
    [InlineData("""{"is_subflow":"true"}""")]
    [InlineData("""{"is_subflow":1}""")]
    [InlineData("""{"is_subflow":null}""")]
    [InlineData("null")]
    public async Task OnlyABooleanTrueCounts(string metadata)
    {
        using var f = new Fixture();
        f.SeedWorkflow(metadata: metadata);

        Assert.Empty(Subflows(await f.Build().ListSubflows(ct: default)));
    }

    [Fact]
    public async Task SubflowsCanBeFilteredByEnvironment()
    {
        using var f = new Fixture();
        f.SeedWorkflow(name: "draft-one", environment: "draft", metadata: """{"is_subflow":true}""");
        f.SeedWorkflow(name: "prod-one", environment: "production", metadata: """{"is_subflow":true}""");

        var rows = Subflows(await f.Build().ListSubflows("production", default));

        Assert.Equal("prod-one", Assert.Single(rows).Name);
    }

    [Fact]
    public async Task SubflowsAreOrderedByName()
    {
        using var f = new Fixture();
        f.SeedWorkflow(name: "zeta", metadata: """{"is_subflow":true}""");
        f.SeedWorkflow(name: "alpha", metadata: """{"is_subflow":true}""");

        var rows = Subflows(await f.Build().ListSubflows(ct: default));

        Assert.Equal(new[] { "alpha", "zeta" }, rows.Select(r => r.Name));
    }

    // ─── Export ─────────────────────────────────────────────────────────

    // The filename header is what makes the browser save it correctly.
    [Fact]
    public async Task ExportSetsTheDownloadFilename()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var controller = f.Build();
        controller.ControllerContext.HttpContext = new DefaultHttpContext();

        var result = await controller.Export(id, "yaml", default);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("name: wf", content.Content);
        Assert.Equal("application/x-yaml", content.ContentType);
        Assert.Contains("wf.yaml",
            controller.ControllerContext.HttpContext.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task ExportForwardsTheRequestedFormat()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var controller = f.Build();
        controller.ControllerContext.HttpContext = new DefaultHttpContext();

        await controller.Export(id, "json", default);

        Assert.Equal("json", f.Export.FormatSeen);
    }

    [Fact]
    public async Task ExportDefaultsToYaml()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var controller = f.Build();
        controller.ControllerContext.HttpContext = new DefaultHttpContext();

        await controller.Export(id, ct: default);

        Assert.Equal("yaml", f.Export.FormatSeen);
    }

    // ─── RollbackRisk ───────────────────────────────────────────────────

    [Fact]
    public async Task RollbackRiskForAnUnknownWorkflowIs404()
    {
        using var f = new Fixture();
        var analyzer = new WorkflowRollbackAnalyzer(
            new SnippetRepository(f.Db), Array.Empty<ISnippetHandler>());

        var result = await f.Build().RollbackRisk(Guid.NewGuid(), analyzer, default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task RollbackRiskReturnsAnAnalysisForAKnownWorkflow()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        var analyzer = new WorkflowRollbackAnalyzer(
            new SnippetRepository(f.Db), Array.Empty<ISnippetHandler>());

        var result = await f.Build().RollbackRisk(id, analyzer, default);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
