using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimulationResultModel = flow_weaver_backend.Models.SimulationResult;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowVersionModel = flow_weaver_backend.Models.WorkflowVersion;

namespace flow_weaver_backend.Tests;

// Promotion is the gate between a draft and production, and it stacks five
// independent checks: the path is one-way, production needs a second person,
// draft→qa needs a simulation that still matches the current DAG, granular
// grants are re-checked against the TARGET environment, and policies
// get the last word. Each one is the only thing standing between an unreviewed
// change and production.
public class PromotionGateTests
{

    private sealed class ScopedPermissions : IEffectivePermissions
    {
        public string? AllowedEnvironment { get; set; }
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

    private sealed class ScriptedPolicy : IPolicyEvaluator
    {
        public PolicyDecision Decision { get; set; } = new(true, null, null);
        public List<PolicyEvaluationContext> Seen { get; } = new();
        public Task<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken ct)
        {
            Seen.Add(context);
            return Task.FromResult(Decision);
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

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ScopedPermissions Permissions { get; } = new();
        public ScriptedPolicy Policy { get; } = new();

        public PromotionService Build(string rbacMode = "legacy") => new(
            new WorkflowRepository(Db),
            new WorkflowVersionRepository(Db),
            new RepositoryBase<SimulationResultModel>(Db),
            new UnitOfWork(Db),
            new FakeUser(),
            new FakeAudit(),
            new FakeTrace(),
            Policy,
            new WorkflowRollbackAnalyzer(new SnippetRepository(Db), Array.Empty<ISnippetHandler>()),
            Permissions,
            new FixedSettings(rbacMode),
            NullLogger<PromotionService>.Instance);

        public Guid SeedWorkflow(
            string environment = "draft", string nodes = "[]", string edges = "[]",
            Guid? lastSimulationId = null)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Version = 1,
                SchemaVersion = "v1",
                Environment = environment,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element(edges),
                Metadata = TestJson.Element("{}"),
                LastSimulationId = lastSimulationId,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        // A simulation whose schema hash matches the workflow's current graph.
        public Guid SeedSimulation(Guid workflowId, string nodes, string edges, bool ok = true, string? hash = null)
        {
            var id = Guid.NewGuid();
            Db.Set<SimulationResultModel>().Add(new SimulationResultModel
            {
                SimulationResultId = id,
                WorkflowId = workflowId,
                Ok = ok,
                SchemaHash = hash ?? SimulateWorkflowRunHandler.ComputeSchemaHash(
                    TestJson.Element(nodes), TestJson.Element(edges)),
                IsActive = true,
            });
            Db.SaveChanges();

            var wf = Db.Set<WorkflowModel>().Single(w => w.WorkflowId == workflowId);
            wf.LastSimulationId = id;
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static PromoteRequest Promote(
        string target = "qa", string? approvedBy = null, string? promotedBy = "alice")
        => new() { TargetEnvironment = target, ApprovedBy = approvedBy, PromotedBy = promotedBy };

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsType<ObjectResult>(result.Result).StatusCode!.Value;

    // A successful promotion answers 201 Created with the promoted copy.
    private static WorkflowResponse Promoted(ActionResult<WorkflowResponse> result)
    {
        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        return Assert.IsType<WorkflowResponse>(created.Value);
    }

    // ─── the promotion path is one-way ──────────────────────────────────

    [Fact]
    public async Task PromotingAnUnknownWorkflowIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().PromoteAsync(Guid.NewGuid(), Promote(), default)));
    }

    // Draft is where work starts; you cannot promote INTO it.
    [Theory]
    [InlineData("draft")]
    [InlineData("staging")]
    [InlineData("")]
    public async Task AnInvalidTargetEnvironmentIsRejected(string target)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        Assert.Equal(400, StatusOf(await f.Build().PromoteAsync(id, Promote(target: target), default)));
    }

    // Backwards and sideways moves are both refused — promotion only ever
    // moves forward along draft → qa → production.
    [Theory]
    [InlineData("production", "qa")]
    [InlineData("qa", "qa")]
    [InlineData("production", "production")]
    public async Task PromotingBackwardsOrSidewaysIsRejected(string from, string to)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: from);

        var result = await f.Build().PromoteAsync(id, Promote(target: to, approvedBy: "bob"), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Single(f.Db.Set<WorkflowModel>());   // nothing was copied
    }

    // ─── the four-eyes rule for production ──────────────────────────────

    [Fact]
    public async Task PromotingToProductionRequiresAnApprover()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build().PromoteAsync(id, Promote("production", approvedBy: null), default);

        Assert.Equal(400, StatusOf(result));
    }

    // The approver must be a second person — self-approval defeats the review.
    // The promoter is the authenticated caller ("tester" in FakeUser).
    [Theory]
    [InlineData("tester")]
    [InlineData(" Tester ")]
    public async Task TheApproverMustDifferFromThePromoter(string approvedBy)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build().PromoteAsync(
            id, Promote("production", approvedBy: approvedBy, promotedBy: null), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Single(f.Db.Set<WorkflowModel>());
    }

    // A client-sent promoted_by cannot stand in for the caller: naming someone
    // else there must not let the caller approve their own promotion.
    [Fact]
    public async Task AClientSentPromoterCannotBypassTheTwoPersonRule()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build().PromoteAsync(
            id, Promote("production", approvedBy: "tester", promotedBy: "alice"), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Single(f.Db.Set<WorkflowModel>());
    }

    [Fact]
    public async Task ADistinctApproverIsAccepted()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build().PromoteAsync(
            id, Promote("production", approvedBy: "bob", promotedBy: "alice"), default);

        Assert.Equal("production", Promoted(result).Environment);
    }

    // qa→production carries no simulation gate; that one is draft→qa only.
    [Fact]
    public async Task QaToProductionDoesNotRequireASimulation()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa", lastSimulationId: null);

        var result = await f.Build().PromoteAsync(
            id, Promote("production", approvedBy: "bob"), default);

        Assert.Equal("production", Promoted(result).Environment);
    }

    // ─── the simulation gate (draft → qa) ───────────────────────────────

    // "No victory until verified": a draft that was never simulated cannot
    // reach qa.
    [Fact]
    public async Task DraftToQaWithoutASimulationIsBlocked()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "draft", lastSimulationId: null);

        var result = await f.Build().PromoteAsync(id, Promote("qa"), default);

        Assert.Equal(412, StatusOf(result));
    }

    [Fact]
    public async Task DraftToQaWithAMatchingSimulationIsAllowed()
    {
        using var f = new Fixture();
        const string nodes = """[{"id":"n1","snippet_id":"s1"}]""";
        const string edges = """[{"source":"n1","target":"n2"}]""";
        var id = f.SeedWorkflow(environment: "draft", nodes: nodes, edges: edges);
        f.SeedSimulation(id, nodes, edges);

        var result = await f.Build().PromoteAsync(id, Promote("qa"), default);

        Assert.Equal("qa", Promoted(result).Environment);
    }

    // The hash check is what catches "simulated, then edited the graph, then
    // promoted" — the simulation no longer describes what would run.
    [Fact]
    public async Task ASimulationForADifferentGraphIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "draft", nodes: """[{"id":"n1"}]""");
        f.SeedSimulation(id, """[{"id":"OTHER"}]""", "[]");

        var result = await f.Build().PromoteAsync(id, Promote("qa"), default);

        Assert.Equal(412, StatusOf(result));
    }

    // A simulation that FAILED is not a pass.
    [Fact]
    public async Task AFailedSimulationDoesNotSatisfyTheGate()
    {
        using var f = new Fixture();
        const string nodes = """[{"id":"n1"}]""";
        var id = f.SeedWorkflow(environment: "draft", nodes: nodes);
        f.SeedSimulation(id, nodes, "[]", ok: false);

        Assert.Equal(412, StatusOf(await f.Build().PromoteAsync(id, Promote("qa"), default)));
    }

    // ─── granular RBAC re-check ─────────────────────────────────────────

    // The coarse controller attribute only proves "can promote at all"; a
    // grant scoped to qa must not reach production.
    [Fact]
    public async Task GranularModeRefusesATargetTheGrantDoesNotCover()
    {
        using var f = new Fixture();
        f.Permissions.AllowedEnvironment = "qa";
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build(RbacModes.Granular).PromoteAsync(
            id, Promote("production", approvedBy: "bob"), default);

        Assert.Equal(403, StatusOf(result));
        Assert.Single(f.Db.Set<WorkflowModel>());
    }

    // The condition is checked against the TARGET, not the current
    // environment — otherwise a qa-only grant would authorise leaving qa.
    [Fact]
    public async Task TheCheckUsesTheTargetEnvironment()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build(RbacModes.Granular).PromoteAsync(
            id, Promote("production", approvedBy: "bob"), default);

        var ctx = Assert.Single(f.Permissions.Checked);
        Assert.Equal("production", ctx.Environment);
        Assert.Equal("workflow", ctx.ResourceType);
        Assert.Equal(id, ctx.ResourceId);
    }

    [Fact]
    public async Task LegacyModeSkipsTheScopedCheck()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build("legacy").PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        Assert.Empty(f.Permissions.Checked);
    }

    // ─── policy gate ────────────────────────────────────────────────────

    // Policies get the last word — the seeded default enforces the "recent
    // successful run" rule for qa→production without admin work.
    [Fact]
    public async Task APolicyDenialBlocksThePromotion()
    {
        using var f = new Fixture();
        f.Policy.Decision = new PolicyDecision(false, "48h-run-rule", "no successful run in the last 48h");
        var id = f.SeedWorkflow(environment: "qa");

        var result = await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        Assert.Equal(412, StatusOf(result));
        Assert.Single(f.Db.Set<WorkflowModel>());
    }

    // The evaluator needs the target to apply an env-specific rule.
    [Fact]
    public async Task ThePolicyContextCarriesBothEnvironments()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        var ctx = Assert.Single(f.Policy.Seen);
        Assert.Equal("promote", ctx.Action);
        Assert.Equal("qa", ctx.Environment);            // where it is now
        Assert.Equal("production", ctx.TargetEnvironment);
        Assert.Equal(id, ctx.WorkflowId);
    }

    // ─── the promotion itself ───────────────────────────────────────────

    // Promotion COPIES: the source keeps living in its own environment so the
    // author can keep iterating.
    [Fact]
    public async Task PromotionCreatesACopyAndLeavesTheSourceInPlace()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        var all = await f.Db.Set<WorkflowModel>().ToListAsync();
        Assert.Equal(2, all.Count);
        var source = all.Single(w => w.WorkflowId == id);
        Assert.Equal("qa", source.Environment);
        var promoted = all.Single(w => w.WorkflowId != id);
        Assert.Equal("production", promoted.Environment);
        Assert.Equal(id, promoted.PromotedFrom);
    }

    // The graph must survive the copy verbatim — a promotion that changed the
    // workflow would defeat the review.
    [Fact]
    public async Task ThePromotedCopyCarriesTheSameGraph()
    {
        using var f = new Fixture();
        const string nodes = """[{"id":"n1","snippet_id":"s1"}]""";
        var id = f.SeedWorkflow(environment: "qa", nodes: nodes);

        await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        var promoted = await f.Db.Set<WorkflowModel>().SingleAsync(w => w.WorkflowId != id);
        Assert.Equal("n1", promoted.Nodes[0].GetProperty("id").GetString());
        Assert.Equal("v1", promoted.SchemaVersion);
    }

    // Promoting the same workflow a second time is an ordinary thing to do —
    // edit the draft, promote to qa again; or ship qa to production twice.
    // `workflow_versions` has a UNIQUE (WorkflowId, Version), so the snapshot
    // version has to move, or Postgres answers 23505 and the operator gets a
    // 500. EF InMemory does NOT enforce unique indexes, so asserting "it did
    // not throw" would prove nothing here — assert the pairs are distinct,
    // which is exactly what the constraint demands.
    [Fact]
    public async Task PromotingTheSameWorkflowTwiceProducesDistinctVersions()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);
        await f.Build().PromoteAsync(id, Promote("production", approvedBy: "bob"), default);

        var pairs = await f.Db.Set<WorkflowVersionModel>()
            .Where(v => v.WorkflowId == id)
            .Select(v => v.Version)
            .ToListAsync();

        Assert.Equal(2, pairs.Count);
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    // The same collision on the draft→qa leg, which is the one people hit
    // most: promote, notice something, fix the draft, promote again.
    [Fact]
    public async Task PromotingADraftTwiceProducesDistinctVersions()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();
        f.SeedSimulation(id, "[]", "[]");

        await f.Build().PromoteAsync(id, Promote(), default);
        await f.Build().PromoteAsync(id, Promote(), default);

        var versions = await f.Db.Set<WorkflowVersionModel>()
            .Where(v => v.WorkflowId == id)
            .Select(v => v.Version)
            .ToListAsync();

        Assert.Equal(2, versions.Count);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    // A version snapshot is what rollback later restores.
    [Fact]
    public async Task PromotionSnapshotsAVersion()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build().PromoteAsync(
            id, new PromoteRequest
            {
                TargetEnvironment = "production",
                ApprovedBy = "bob",
                PromotedBy = "alice",
                ChangeSummary = "adds the ACL step",
            }, default);

        var version = await f.Db.Set<WorkflowVersionModel>().SingleAsync();
        Assert.Equal(id, version.WorkflowId);
        // The authenticated caller is recorded, not the client-sent name.
        Assert.Equal("tester", version.PromotedBy);
        Assert.Equal("adds the ACL step", version.ChangeSummary);
        Assert.True(version.PromotedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    // The acting user is recorded, so the audit trail is never blank.
    [Fact]
    public async Task TheActingUserIsRecordedWhenNoPromoterIsGiven()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment: "qa");

        await f.Build().PromoteAsync(
            id, new PromoteRequest { TargetEnvironment = "production", ApprovedBy = "bob", PromotedBy = null },
            default);

        Assert.Equal("tester", (await f.Db.Set<WorkflowVersionModel>().SingleAsync()).PromotedBy);
    }

}
