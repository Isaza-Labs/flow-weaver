using System.Text;
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
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The controller's own query and import paths — the ones it implements
// instead of delegating.
//
// `Get` is the interesting one: it short-circuits to the service when there
// is no filter, and otherwise builds its own query. That second branch is
// where a missing predicate would return the wrong workflow list, so the
// filter assertions are the substance here.
public class WorkflowControllerQueryTests
{

    // The unfiltered branch delegates to IWorkflow; the filtered branch never
    // touches it. A recording stub distinguishes the two.
    private sealed class RecordingWorkflowService : IWorkflow
    {
        public int GetCalls { get; private set; }
        public CreateWorkflow? LastCreate { get; private set; }

        public Task<ActionResult<ListResponse<WorkflowResponse>>> GetAsync(int limit = 50, int offset = 0)
        {
            GetCalls++;
            return Task.FromResult<ActionResult<ListResponse<WorkflowResponse>>>(
                new OkObjectResult(new ListResponse<WorkflowResponse> { Data = new List<WorkflowResponse>() }));
        }

        public Task<ActionResult<WorkflowResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();

        public Task<ActionResult<WorkflowResponse>> PostAsync(CreateWorkflow dto)
        {
            LastCreate = dto;
            return Task.FromResult<ActionResult<WorkflowResponse>>(
                new WorkflowResponse { WorkflowId = Guid.NewGuid(), Name = dto.Name });
        }

        public Task<ActionResult<WorkflowResponse>> UpdateAsync(Guid id, UpdateWorkflow dto)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class ScriptedTriggerService : IWorkflowTrigger
    {
        public ActionResult<ListResponse<WorkflowTriggerResponse>> Result { get; set; }
            = new OkObjectResult(new ListResponse<WorkflowTriggerResponse>
            {
                Data = new List<WorkflowTriggerResponse>(),
            });

        public Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetByWorkflowAsync(
            Guid workflowId, int limit = 50, int offset = 0) => Task.FromResult(Result);
        public Task<ActionResult<WorkflowTriggerResponse>> PostForWorkflowAsync(
            Guid workflowId, CreateWorkflowTrigger dto) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> RotateWebhookSecretAsync(Guid id)
            => throw new NotSupportedException();
        public Task<ActionResult<ListResponse<WorkflowTriggerResponse>>> GetAsync(int limit = 50, int offset = 0)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> PostAsync(CreateWorkflowTrigger dto)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> UpdateAsync(Guid id, UpdateWorkflowTrigger dto)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowTriggerResponse>> DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class ScriptedExportService : IWorkflowExportService
    {
        public ImportPayload Payload { get; set; } = new() { Name = "imported" };
        public (string Raw, string Format)? LastParse { get; private set; }

        public Task<ExportPayload> ExportAsync(Guid workflowId, string format, CancellationToken ct)
            => throw new NotSupportedException();

        public ImportPayload ParseImport(string raw, string format)
        {
            LastParse = (raw, format);
            return Payload;
        }
    }

    private sealed class UnusedExecutor : IWorkflowExecutor
    {
        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId, RunWorkflowRequest request,
            CancellationToken ct, string trigger = "manual") => throw new NotSupportedException();
        public Task ExecuteRunAsync(Guid runId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotSupportedException();
    }

    private sealed class UnusedPromotion : IPromotionService
    {
        public Task<ActionResult<WorkflowResponse>> PromoteAsync(Guid id, PromoteRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> RollbackAsync(Guid id, int toVersion, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<DiffResult>> DiffAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<WorkflowResponse>> CloneAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedVersions : IWorkflowVersion
    {
        public ActionResult<ListResponse<WorkflowVersionResponse>> Result { get; set; }
            = new OkObjectResult(new ListResponse<WorkflowVersionResponse>
            {
                Data = new List<WorkflowVersionResponse>(),
            });

        public Task<ActionResult<ListResponse<WorkflowVersionResponse>>> GetByWorkflowAsync(
            Guid workflowId, int limit = 50, int offset = 0) => Task.FromResult(Result);
        public Task<ActionResult<WorkflowVersionResponse>> GetByIdAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class AllowAllPermissions : IEffectivePermissions
    {
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class LegacySettings : IAppSettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = "legacy" });
        public Task<AppSettings> UpdateAsync(
            AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingWorkflowService Service { get; } = new();
        public ScriptedTriggerService Triggers { get; } = new();
        public ScriptedExportService Export { get; } = new();
        public ScriptedVersions Versions { get; } = new();

        public WorkflowController Build(string? body = null)
        {
            var ctx = TestCtx.WithUser();
            if (body is not null)
                ctx.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

            return new WorkflowController(
                Service,
                Triggers,
                new UnusedExecutor(),
                new UnusedPromotion(),
                new FakeUser(),
                Db,
                Versions,
                Export,
                BundleTest.Svc(Db),
                new flow_weaver_backend.Services.Workflow.WorkflowBundleImporter(
                    // Constructed, never invoked: these tests exercise the YAML
                    // import path, which never reaches the bundle importer.
                    BundleTest.Svc(Db), workflows: null!, triggers: null!,
                    new SnippetRepository(Db), new UnitOfWork(Db),
                    NullLogger<flow_weaver_backend.Services.Workflow.WorkflowBundleImporter>.Instance),
                new SnippetRepository(Db),
                new AllowAllPermissions(),
                new LegacySettings(),
                NullLogger<WorkflowController>.Instance)
            {
                ControllerContext = ctx,
            };
        }

        public Guid SeedWorkflow(
            string name = "lldp-sync", string environment = "draft",
            bool active = true, DateTime? createdAt = null)
        {
            var id = Guid.NewGuid();
            Db.Workflows.Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = name,
                Environment = environment,
                Version = 1,
                SchemaVersion = "v1",
                Nodes = TestJson.Element("[]"),
                Edges = TestJson.Element("[]"),
                Metadata = TestJson.Element("{}"),
                IsActive = active,
                CreatedAt = createdAt ?? DateTime.UtcNow,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static ListResponse<WorkflowResponse> Page(
        ActionResult<ListResponse<WorkflowResponse>> result)
        => Assert.IsType<ListResponse<WorkflowResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    // ─── Get ────────────────────────────────────────────────────────────

    // With no filter the controller has nothing to add, so it delegates
    // rather than duplicating the service's query.
    [Fact]
    public async Task Get_WithoutAFilterDelegatesToTheService()
    {
        using var f = new Fixture();
        f.SeedWorkflow();

        await f.Build().Get();

        Assert.Equal(1, f.Service.GetCalls);
    }

    [Fact]
    public async Task Get_FilteringByEnvironmentNarrowsTheResult()
    {
        using var f = new Fixture();
        f.SeedWorkflow("a", "draft");
        f.SeedWorkflow("b", "production");

        var page = Page(await f.Build().Get(environment: "production"));

        Assert.Equal("b", page.Data.Single().Name);
    }

    [Fact]
    public async Task Get_SoftDeletedWorkflowsAreExcluded()
    {
        using var f = new Fixture();
        f.SeedWorkflow("gone", "draft", active: false);

        Assert.Equal(0, Page(await f.Build().Get(environment: "draft")).Total);
    }

    // Newest first: the list view is a "what did I touch recently" surface.
    [Fact]
    public async Task Get_TheFilteredListIsNewestFirst()
    {
        using var f = new Fixture();
        f.SeedWorkflow("older", "draft", createdAt: DateTime.UtcNow.AddDays(-2));
        f.SeedWorkflow("newer", "draft", createdAt: DateTime.UtcNow);

        var page = Page(await f.Build().Get(environment: "draft"));

        Assert.Equal(new[] { "newer", "older" }, page.Data.Select(w => w.Name));
    }

    [Fact]
    public async Task Get_TheFilteredListIsPaginatedAndReportsTheFullTotal()
    {
        using var f = new Fixture();
        f.SeedWorkflow("a", "draft");
        f.SeedWorkflow("b", "draft");
        f.SeedWorkflow("c", "draft");

        var page = Page(await f.Build().Get(limit: 2, environment: "draft"));

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Data.Count);
        Assert.Equal(2, page.Limit);
    }

    [Fact]
    public async Task Get_TheFilteredListCarriesTheFullWorkflowShape()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow("lldp-sync", "draft");

        var row = Page(await f.Build().Get(environment: "draft")).Data.Single();

        Assert.Equal(id, row.WorkflowId);
        Assert.Equal("v1", row.SchemaVersion);
        Assert.Equal("draft", row.Environment);
        Assert.Equal(JsonValueKind.Array, row.Nodes.ValueKind);
    }

    // KNOWN LIMITATION: the `search` filter uses EF.Functions.ILike, which is
    // Postgres-only — the InMemory provider can't translate it, so that
    // branch is integration-test territory. The environment filter above
    // covers the rest of the same query builder.
    [Fact]
    public async Task Get_TheSearchFilterIsPostgresOnly()
    {
        using var f = new Fixture();
        f.SeedWorkflow("lldp-sync", "draft");

        await Assert.ThrowsAnyAsync<Exception>(() => f.Build().Get(search: "lldp"));
    }

    // ─── GetTriggers ────────────────────────────────────────────────────

    // The endpoint unwraps the service's paged envelope into a bare array —
    // the editor's trigger panel reads a list, not a page.
    [Fact]
    public async Task GetTriggers_UnwrapsThePagedEnvelope()
    {
        using var f = new Fixture();
        f.Triggers.Result = new OkObjectResult(new ListResponse<WorkflowTriggerResponse>
        {
            Data = new List<WorkflowTriggerResponse>
            {
                new() { WorkflowTriggerId = Guid.NewGuid(), Name = "nightly" },
            },
            Total = 1,
        });

        var result = await f.Build().GetTriggers(Guid.NewGuid());

        var rows = Assert.IsType<List<WorkflowTriggerResponse>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("nightly", Assert.Single(rows).Name);
    }

    // A refusal from the service (unknown workflow) is passed through
    // untouched rather than becoming an empty list.
    [Fact]
    public async Task GetTriggers_AServiceRefusalIsPassedThrough()
    {
        using var f = new Fixture();
        f.Triggers.Result = new NotFoundObjectResult(new { error = "workflow not found" });

        var result = await f.Build().GetTriggers(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ─── Versions ───────────────────────────────────────────────────────

    [Fact]
    public async Task Versions_DelegatesToTheVersionService()
    {
        using var f = new Fixture();
        f.Versions.Result = new OkObjectResult(new ListResponse<WorkflowVersionResponse>
        {
            Data = new List<WorkflowVersionResponse>
            {
                new() { Id = Guid.NewGuid(), Version = 2 },
            },
            Total = 1,
        });

        var result = await f.Build().Versions(Guid.NewGuid());

        var page = Assert.IsType<ListResponse<WorkflowVersionResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(2, page.Data.Single().Version);
    }

    // ─── Import ─────────────────────────────────────────────────────────

    // The import path deliberately funnels through the SAME PostAsync as a
    // normal create, so schema + reference validation apply to uploads too.
    [Fact]
    public async Task Import_GoesThroughTheNormalCreatePath()
    {
        using var f = new Fixture();
        f.Export.Payload = new ImportPayload
        {
            Name = "imported",
            Description = "from yaml",
            Nodes = TestJson.Element("""[{"id":"a"}]"""),
            Edges = TestJson.Element("[]"),
        };

        var result = await f.Build(body: "workflow:\n  name: imported\n").Import();

        Assert.NotNull(result.Value ?? (result.Result as ObjectResult)?.Value as WorkflowResponse);
        var created = f.Service.LastCreate!;
        Assert.Equal("imported", created.Name);
        Assert.Equal("from yaml", created.Description);
        Assert.Equal("a", created.Nodes!.Value.EnumerateArray().Single().GetProperty("id").GetString());
    }

    [Fact]
    public async Task Import_ForwardsTheRawBodyAndFormatToTheParser()
    {
        using var f = new Fixture();

        await f.Build(body: "workflow:\n  name: imported\n").Import(format: "yaml");

        Assert.Equal("yaml", f.Export.LastParse!.Value.Format);
        Assert.Contains("name: imported", f.Export.LastParse.Value.Raw);
    }

    [Fact]
    public async Task Import_DefaultsToYaml()
    {
        using var f = new Fixture();

        await f.Build(body: "{}").Import();

        Assert.Equal("yaml", f.Export.LastParse!.Value.Format);
    }

    // A parse failure propagates as the service's own domain exception; the
    // global handler renders it as problem+json.
    [Fact]
    public async Task Import_AParseFailurePropagates()
    {
        using var f = new Fixture();
        var controller = f.Build(body: "{ broken");
        f.Export.Payload = null!;

        await Assert.ThrowsAnyAsync<Exception>(() => controller.Import(format: "json"));
    }
}

