using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimulationResultModel = flow_weaver_backend.Models.SimulationResult;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// Diff and Clone. The diff is what a reviewer reads before approving a
// promotion to production, so "no changes" must mean no changes — a missed
// edge or a config-only edit that reads as identical would let an unreviewed
// change through the gate.
public class PromotionDiffCloneTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public PromotionService Build()
        {
            var caller = new FakeUser();
            return new PromotionService(
                new WorkflowRepository(Db),
                new WorkflowVersionRepository(Db),
                new RepositoryBase<SimulationResultModel>(Db),
                new UnitOfWork(Db),
                caller,
                new FakeAudit(),
                new FakeTrace(),
                new FakePolicyEvaluator(),
                new WorkflowRollbackAnalyzer(new SnippetRepository(Db), Array.Empty<ISnippetHandler>()),
                new EffectivePermissions(caller, new PermissionGrantReader(Db)),
                new FakeAppSettings(),
                NullLogger<PromotionService>.Instance);
        }

        public Guid SeedWorkflow(
            string nodes = "[]", string edges = "[]", string name = "wf",
            string environment = "draft", Guid? promotedFrom = null)
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = name,
                Description = "a description",
                Version = 1,
                SchemaVersion = "v1",
                Environment = environment,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element(edges),
                Metadata = TestJson.Element("""{"tag":"x"}"""),
                PromotedFrom = promotedFrom,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static DiffResult Diff(ActionResult<DiffResult> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<DiffResult>(result.Value);
    }

    private const string TwoNodes = """
        [{"id":"n1","snippet_id":"s1","config_overrides":{"cmd":"show ver"}},
         {"id":"n2","snippet_id":"s2"}]
        """;

    // ─── diff against nothing ───────────────────────────────────────────

    // With no promoted copy every node reads as added — the reviewer is seeing
    // the whole workflow for the first time.
    [Fact]
    public async Task Diff_WithoutAPromotedCopyEverythingIsAdded()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes, edges: """[{"source":"n1","target":"n2"}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.Equal(2, diff.NodesAdded.Count);
        Assert.Single(diff.EdgesAdded);
        Assert.Empty(diff.NodesRemoved);
        Assert.Empty(diff.NodesChanged);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public async Task Diff_EmptyWorkflowWithoutAPromotedCopyHasNoChanges()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public async Task Diff_UnknownWorkflowIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().DiffAsync(Guid.NewGuid(), default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ─── diff against a promoted copy ───────────────────────────────────

    // Seeds a draft plus a promoted copy of it, so the diff has a baseline.
    private static Guid SeedPair(Fixture f, string draftNodes, string promotedNodes,
        string draftEdges = "[]", string promotedEdges = "[]")
    {
        var draftId = f.SeedWorkflow(nodes: draftNodes, edges: draftEdges);
        f.SeedWorkflow(nodes: promotedNodes, edges: promotedEdges,
            environment: "production", promotedFrom: draftId);
        return draftId;
    }

    [Fact]
    public async Task Diff_IdenticalWorkflowsReportNoChanges()
    {
        using var f = new Fixture();
        var id = SeedPair(f, TwoNodes, TwoNodes,
            """[{"source":"n1","target":"n2"}]""", """[{"source":"n1","target":"n2"}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.False(diff.HasChanges);
        Assert.Empty(diff.NodesAdded);
        Assert.Empty(diff.NodesRemoved);
        Assert.Empty(diff.NodesChanged);
        Assert.Empty(diff.EdgesAdded);
        Assert.Empty(diff.EdgesRemoved);
    }

    [Fact]
    public async Task Diff_DetectsAnAddedNode()
    {
        using var f = new Fixture();
        var id = SeedPair(f, TwoNodes, """[{"id":"n1","snippet_id":"s1","config_overrides":{"cmd":"show ver"}}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.Equal("n2", Assert.Single(diff.NodesAdded).GetProperty("id").GetString());
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public async Task Diff_DetectsARemovedNode()
    {
        using var f = new Fixture();
        var id = SeedPair(f, """[{"id":"n1","snippet_id":"s1","config_overrides":{"cmd":"show ver"}}]""", TwoNodes);

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.Equal("n2", Assert.Single(diff.NodesRemoved).GetProperty("id").GetString());
    }

    // A config-only edit is exactly the change a reviewer must not miss.
    [Fact]
    public async Task Diff_DetectsAConfigOnlyChangeWithBeforeAndAfter()
    {
        using var f = new Fixture();
        var id = SeedPair(
            f,
            """[{"id":"n1","config_overrides":{"cmd":"reload"}}]""",
            """[{"id":"n1","config_overrides":{"cmd":"show ver"}}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        var change = Assert.Single(diff.NodesChanged);
        Assert.Equal("n1", change.NodeId);
        Assert.Equal("show ver", change.Before.GetProperty("config_overrides").GetProperty("cmd").GetString());
        Assert.Equal("reload", change.After.GetProperty("config_overrides").GetProperty("cmd").GetString());
    }

    [Fact]
    public async Task Diff_DetectsEdgeChanges()
    {
        using var f = new Fixture();
        var id = SeedPair(f, TwoNodes, TwoNodes,
            draftEdges: """[{"source":"n1","target":"n2","type":"failure"}]""",
            promotedEdges: """[{"source":"n1","target":"n2","type":"success"}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.Single(diff.EdgesAdded);
        Assert.Single(diff.EdgesRemoved);
        Assert.True(diff.HasChanges);
    }

    // A node with no id can't be diffed by identity; it must not crash the
    // comparison or masquerade as a change.
    [Fact]
    public async Task Diff_IgnoresNodesWithoutAnId()
    {
        using var f = new Fixture();
        var id = SeedPair(f, """[{"snippet_id":"s1"}]""", """[{"snippet_id":"s1"}]""");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public async Task Diff_HandlesNonArrayNodesWithoutThrowing()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: """{"not":"an array"}""", edges: "null");

        var diff = Diff(await f.Build().DiffAsync(id, default));

        Assert.False(diff.HasChanges);
    }

    // ─── clone ──────────────────────────────────────────────────────────

    // Clone answers 201 Created with the new workflow in an ObjectResult.
    private static WorkflowResponse Ok(ActionResult<WorkflowResponse> result)
    {
        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        return Assert.IsType<WorkflowResponse>(created.Value);
    }

    // A clone of a production workflow must land in draft — otherwise cloning
    // would be a way to mint production workflows without promotion.
    [Fact]
    public async Task Clone_AlwaysLandsInDraftAtVersionOne()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes, environment: "production");

        var body = Ok(await f.Build().CloneAsync(id, default));

        Assert.Equal("draft", body.Environment);
        Assert.Equal(1, body.Version);
    }

    [Fact]
    public async Task Clone_CopiesTheGraphAndNamesItACopy()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes, edges: """[{"source":"n1","target":"n2"}]""", name: "deploy");

        var body = Ok(await f.Build().CloneAsync(id, default));

        Assert.Equal("deploy (copy)", body.Name);
        var clone = await f.Db.Set<WorkflowModel>().SingleAsync(w => w.WorkflowId == body.WorkflowId);
        Assert.Equal(2, clone.Nodes.GetArrayLength());
        Assert.Single(clone.Edges.EnumerateArray());
        Assert.Equal("a description", clone.Description);
        Assert.Equal("v1", clone.SchemaVersion);
    }

    // The provenance note is what lets an operator trace a stray copy back.
    [Fact]
    public async Task Clone_RecordsItsProvenance()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes);

        var body = Ok(await f.Build().CloneAsync(id, default));

        var clone = await f.Db.Set<WorkflowModel>().SingleAsync(w => w.WorkflowId == body.WorkflowId);
        Assert.Contains(id.ToString(), clone.ChangeSummary);
    }

    [Fact]
    public async Task Clone_LeavesTheOriginalUntouched()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes, name: "deploy", environment: "production");

        await f.Build().CloneAsync(id, default);

        var original = await f.Db.Set<WorkflowModel>().SingleAsync(w => w.WorkflowId == id);
        Assert.Equal("deploy", original.Name);
        Assert.Equal("production", original.Environment);
    }

    [Fact]
    public async Task Clone_UnknownWorkflowIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().CloneAsync(Guid.NewGuid(), default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(f.Db.Set<WorkflowModel>());
    }

    // Cloning twice yields two independent copies, not a collision.
    [Fact]
    public async Task Clone_IsRepeatable()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(nodes: TwoNodes);
        var svc = f.Build();

        var first = Ok(await svc.CloneAsync(id, default));
        var second = Ok(await svc.CloneAsync(id, default));

        Assert.NotEqual(first.WorkflowId, second.WorkflowId);
        Assert.Equal(3, await f.Db.Set<WorkflowModel>().CountAsync());
    }
}
