using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SimulationResultModel = flow_weaver_backend.Models.SimulationResult;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowVersionModel = flow_weaver_backend.Models.WorkflowVersion;

namespace flow_weaver_backend.Tests;

// Rollback and clone. Rollback is the risky one: "undo" is a lie when a step
// already deleted a VLAN or sent an email, so the analyzer refuses to roll
// back a graph containing non-reversible nodes and tells the author to build a
// forward fix instead.
//
// The other property worth pinning is that rollback never mutates history — it
// creates a NEW draft carrying the old graph, so the promoted rows and their
// audit trail stay intact.
public class PromotionRollbackTests
{

    private sealed class AllowAllPermissions : IEffectivePermissions
    {
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class AllowAllPolicy : IPolicyEvaluator
    {
        public Task<PolicyDecision> EvaluateAsync(
            PolicyEvaluationContext context, CancellationToken ct)
            => Task.FromResult(new PolicyDecision(true, null, null));
    }

    private sealed class LegacySettings : IAppSettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = "legacy" });
        public Task<AppSettings> UpdateAsync(
            AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    // Declares a fixed idempotency per snippet type so the rollback analyzer
    // can classify a snapshot's nodes without the real worker handlers.
    private sealed class FixedHandler : ISnippetHandler
    {
        public FixedHandler(string type, IdempotencyKind kind)
        {
            Type = type;
            DefaultIdempotency = kind;
        }
        public string Type { get; }
        public IdempotencyKind DefaultIdempotency { get; }
        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RecordingAudit : flow_weaver_backend.Services.Audit.IAuditLogger
    {
        public List<string> Actions { get; } = new();
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public List<ISnippetHandler> Handlers { get; } = new();
        public RecordingAudit Audit { get; } = new();

        public PromotionService Build() => new(
            new WorkflowRepository(Db),
            new WorkflowVersionRepository(Db),
            new RepositoryBase<SimulationResultModel>(Db),
            new UnitOfWork(Db),
            new FakeUser(),
            Audit,
            new FakeTrace(),
            new AllowAllPolicy(),
            new WorkflowRollbackAnalyzer(new SnippetRepository(Db), Handlers),
            new AllowAllPermissions(),
            new LegacySettings(),
            NullLogger<PromotionService>.Instance);

        public Guid SeedWorkflow(
            string environment = "production", int version = 3,
            string nodes = "[]", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Workflows.Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "lldp-sync",
                Description = "d",
                Version = version,
                SchemaVersion = "v1",
                Environment = environment,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element("[]"),
                Metadata = TestJson.Element("""{"is_subflow":true}"""),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedVersion(Guid workflowId, int version, string nodes = "[]", string edges = "[]")
        {
            Db.WorkflowVersions.Add(new WorkflowVersionModel
            {
                WorkflowVersionId = Guid.NewGuid(),
                WorkflowId = workflowId,
                Version = version,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element(edges),
                Services = TestJson.Element("{}"),
                PromotedBy = "tester",
                PromotedAt = DateTime.UtcNow,
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public Guid SeedSnippet(string type, string name = "step")
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id, Name = name, Type = type, IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string Node(string id, Guid snippetId)
        => "{\"id\":" + System.Text.Json.JsonSerializer.Serialize(id)
           + ",\"snippet_id\":\"" + snippetId + "\",\"x\":0,\"y\":0}";

    private static int StatusOf(ActionResult<WorkflowResponse> result)
        => Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 0;

    private static WorkflowResponse Body(ActionResult<WorkflowResponse> result)
        => result.Value ?? Assert.IsType<WorkflowResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    // ─── Rollback ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rollback_AnUnknownWorkflowIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().RollbackAsync(Guid.NewGuid(), 1, default)));
    }

    // Asking for a version that was never snapshotted is a 404 naming the
    // version, so the UI can say which one is missing.
    [Fact]
    public async Task Rollback_AMissingSnapshotIs404()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await f.Build().RollbackAsync(id, 2, default);

        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public async Task Rollback_CreatesANewDraftCarryingTheOldGraph()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(version: 3);
        f.SeedVersion(id, 1, nodes: """[{"id":"old"}]""");

        var body = Body(await f.Build().RollbackAsync(id, 1, default));

        // A NEW row: history is never mutated.
        Assert.NotEqual(id, body.WorkflowId);
        Assert.Equal("draft", body.Environment);
        Assert.Equal(4, body.Version);
        Assert.Equal("old", body.Nodes.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Contains("Rolled back to version 1", body.ChangeSummary);
    }

    [Fact]
    public async Task Rollback_LeavesThePromotedRowUntouched()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production", version: 3);
        f.SeedVersion(id, 1);

        await f.Build().RollbackAsync(id, 1, default);

        var original = f.Db.Workflows.Single(w => w.WorkflowId == id);
        Assert.Equal("production", original.Environment);
        Assert.Equal(3, original.Version);
        Assert.True(original.IsActive);
    }

    // The new draft keeps the provenance link so the UI can show where it
    // came from.
    [Fact]
    public async Task Rollback_TheNewDraftPointsBackAtItsSource()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1);

        var body = Body(await f.Build().RollbackAsync(id, 1, default));

        Assert.Equal(id, f.Db.Workflows.Single(w => w.WorkflowId == body.WorkflowId).PromotedFrom);
    }

    // Cosmetic fields ride along so the rolled-back draft is recognisable.
    [Fact]
    public async Task Rollback_KeepsTheNameDescriptionAndMetadata()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1);

        var body = Body(await f.Build().RollbackAsync(id, 1, default));

        Assert.Equal("lldp-sync", body.Name);
        Assert.Equal("d", body.Description);
        Assert.True(body.Metadata.GetProperty("is_subflow").GetBoolean());
    }

    // The core refusal: "undo" is a lie once a step has deleted something.
    [Fact]
    public async Task Rollback_IsBlockedWhenTheSnapshotHasANonReversibleStep()
    {
        using var f = new Fixture();
        f.Handlers.Add(new FixedHandler("ssh", IdempotencyKind.NonReversible));
        var snippetId = f.SeedSnippet("ssh", "delete-vlan");
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1, nodes: "[" + Node("a", snippetId) + "]");

        var result = await f.Build().RollbackAsync(id, 1, default);

        Assert.Equal(409, StatusOf(result));
        // Nothing is created when the rollback is refused.
        Assert.Single(f.Db.Workflows);
    }

    // The refusal names the offending steps AND points at the way forward —
    // an operator staring at "blocked" with no next step is stuck.
    [Fact]
    public async Task Rollback_TheRefusalNamesTheBlockingStepsAndTheWayForward()
    {
        using var f = new Fixture();
        f.Handlers.Add(new FixedHandler("ssh", IdempotencyKind.NonReversible));
        var snippetId = f.SeedSnippet("ssh", "delete-vlan");
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1, nodes: "[" + Node("a", snippetId) + "]");

        var result = await f.Build().RollbackAsync(id, 1, default);

        var problem = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        var text = System.Text.Json.JsonSerializer.Serialize(problem);
        Assert.Contains("delete-vlan", text);
        Assert.Contains("forward fix", text);
    }

    // A blocked rollback still leaves an audit trail — an admin asking "why
    // couldn't we roll back" needs the record.
    [Fact]
    public async Task Rollback_ABlockedAttemptIsAudited()
    {
        using var f = new Fixture();
        f.Handlers.Add(new FixedHandler("ssh", IdempotencyKind.NonReversible));
        var snippetId = f.SeedSnippet("ssh", "delete-vlan");
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1, nodes: "[" + Node("a", snippetId) + "]");

        await f.Build().RollbackAsync(id, 1, default);

        Assert.Contains("rollback.blocked", f.Audit.Actions);
    }

    // A step that merely NEEDS compensation is allowed through — the author
    // is expected to have wired a failure edge, and blocking here would make
    // rollback useless for most real workflows.
    [Fact]
    public async Task Rollback_ACompensatableStepDoesNotBlock()
    {
        using var f = new Fixture();
        f.Handlers.Add(new FixedHandler("rest_call", IdempotencyKind.RequiresCompensation));
        var snippetId = f.SeedSnippet("rest_call", "create-ticket");
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1, nodes: "[" + Node("a", snippetId) + "]");

        var result = await f.Build().RollbackAsync(id, 1, default);

        Assert.Null(result.Result);
    }

    [Fact]
    public async Task Rollback_AnIdempotentSnapshotRollsBackFreely()
    {
        using var f = new Fixture();
        f.Handlers.Add(new FixedHandler("ping", IdempotencyKind.Idempotent));
        var snippetId = f.SeedSnippet("ping");
        var id = f.SeedWorkflow();
        f.SeedVersion(id, 1, nodes: "[" + Node("a", snippetId) + "]");

        Assert.Null((await f.Build().RollbackAsync(id, 1, default)).Result);
    }

    // ─── Clone ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Clone_AnUnknownWorkflowIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().CloneAsync(Guid.NewGuid(), default)));
    }

    // Cloning a promoted row is how an author gets an editable copy — the
    // clone lands in draft at version 1.
    [Fact]
    public async Task Clone_ProducesAFreshDraft()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production", version: 5);

        var body = Body(await f.Build().CloneAsync(id, default));

        Assert.NotEqual(id, body.WorkflowId);
        Assert.Equal("draft", body.Environment);
        Assert.Equal(1, body.Version);
    }

    [Fact]
    public async Task Clone_LeavesTheSourceUntouched()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "production", version: 5);

        await f.Build().CloneAsync(id, default);

        var original = f.Db.Workflows.Single(w => w.WorkflowId == id);
        Assert.Equal("production", original.Environment);
        Assert.Equal(5, original.Version);
    }

}

