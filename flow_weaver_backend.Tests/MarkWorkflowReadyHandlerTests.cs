using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// `mark_workflow_ready` is the agent's own gate: it refuses to mark a draft
// ready unless a simulation exists, passed, and still matches the current
// graph. Every one of those checks exists because marking a broken workflow
// ready is what unblocks promotion toward real devices — a false "ready" is
// the expensive failure here, a false "not ready" merely asks for a re-run.
public class MarkWorkflowReadyHandlerTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public MarkWorkflowReadyHandler Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            new SimulationResultRepository(Db),
            new FakeUser(),
            NullLogger<MarkWorkflowReadyHandler>.Instance);

        public Guid SeedWorkflow(
            string environment = "draft", Guid? simulationId = null,
            string nodes = "[]", string edges = "[]",
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Workflows.Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "wf",
                Environment = environment,
                Version = 1,
                Nodes = TestJson.Element(nodes),
                Edges = TestJson.Element(edges),
                Metadata = TestJson.Element("{}"),
                LastSimulationId = simulationId,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedSimulation(
            Guid workflowId, bool ok = true, int issueCount = 0,
            string? schemaHash = null, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.SimulationResults.Add(new SimulationResult
            {
                SimulationResultId = id,
                WorkflowId = workflowId,
                Ok = ok,
                IssueCount = issueCount,
                SchemaHash = schemaHash ?? "",
                Issues = TestJson.Element("[]"),
                Warnings = TestJson.Element("[]"),
                SimulatedAt = DateTime.UtcNow,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static string? BlockedReason(JsonElement result)
        => result.TryGetProperty("blocker", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

    private static Task<JsonElement> Run(Fixture f, Guid workflowId)
        => f.Build().ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + workflowId + "\"}"), default);

    // The hash the handler compares against is the same one the simulate tool
    // stamps, so a "still matches" test has to use it rather than a literal.
    private static string HashOf(string nodes, string edges)
        => SimulateWorkflowRunHandler.ComputeSchemaHash(
            TestJson.Element(nodes), TestJson.Element(edges));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"workflow_id":"not-a-guid"}""")]
    [InlineData("""{"workflow_id":123}""")]
    public async Task ABadWorkflowIdIsAnErrorPayload(string args)
    {
        using var f = new Fixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("workflow_id (uuid) is required", Error(result));
    }

    [Fact]
    public async Task AnUnknownWorkflowIsReported()
    {
        using var f = new Fixture();

        Assert.Equal("workflow not found", Error(await Run(f, Guid.NewGuid())));
    }

    [Fact]
    public async Task ASoftDeletedWorkflowIsNotFound()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(active: false);

        Assert.Equal("workflow not found", Error(await Run(f, id)));
    }

    // The tool's whole purpose is unblocking draft→qa, so calling it on a
    // promoted row is a category error, not a no-op.
    [Theory]
    [InlineData("qa")]
    [InlineData("production")]
    public async Task OnlyDraftsCanBeMarkedReady(string environment)
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow(environment);

        var result = await Run(f, id);

        Assert.Equal("not_a_draft", BlockedReason(result));
    }

    // No simulation at all: the agent is told which tool to call first.
    [Fact]
    public async Task AWorkflowWithNoSimulationIsBlocked()
    {
        using var f = new Fixture();
        var id = f.SeedWorkflow();

        var result = await Run(f, id);

        Assert.Equal("no_simulation", BlockedReason(result));
    }

    // A dangling simulation reference (row archived) must not read as "no
    // simulation" — the distinction tells the agent whether to re-run or
    // investigate.
    [Fact]
    public async Task ADanglingSimulationReferenceIsItsOwnBlockReason()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow(simulationId: Guid.NewGuid());

        var result = await Run(f, workflowId);

        Assert.Equal("simulation_missing", BlockedReason(result));
    }

    [Fact]
    public async Task AnArchivedSimulationRowCountsAsMissing()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var simId = f.SeedSimulation(workflowId, active: false);
        f.Db.Workflows.Single().LastSimulationId = simId;
        f.Db.SaveChanges();

        Assert.Equal("simulation_missing", BlockedReason(await Run(f, workflowId)));
    }

    // A simulation that found issues is exactly what this gate exists to
    // catch, and the count tells the agent how much work is left.
    [Fact]
    public async Task AFailedSimulationBlocksAndReportsTheIssueCount()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow();
        var simId = f.SeedSimulation(workflowId, ok: false, issueCount: 3);
        f.Db.Workflows.Single().LastSimulationId = simId;
        f.Db.SaveChanges();

        var result = await Run(f, workflowId);

        Assert.Equal("simulation_failed", BlockedReason(result));
        Assert.Contains("3 issue", result.GetProperty("message").GetString());
    }

    // The staleness check is the subtle one: a green simulation from BEFORE
    // an edit says nothing about the graph as it stands now.
    [Fact]
    public async Task ASimulationThatPredatesAnEditIsStale()
    {
        using var f = new Fixture();
        var workflowId = f.SeedWorkflow(nodes: """[{"id":"a"}]""");
        var simId = f.SeedSimulation(workflowId, schemaHash: HashOf("[]", "[]"));
        f.Db.Workflows.Single().LastSimulationId = simId;
        f.Db.SaveChanges();

        Assert.Equal("simulation_stale", BlockedReason(await Run(f, workflowId)));
    }

    [Fact]
    public async Task AGreenMatchingSimulationMarksTheWorkflowReady()
    {
        using var f = new Fixture();
        const string nodes = """[{"id":"a","snippet_id":"__start__"}]""";
        var workflowId = f.SeedWorkflow(nodes: nodes);
        var simId = f.SeedSimulation(workflowId, schemaHash: HashOf(nodes, "[]"));
        f.Db.Workflows.Single().LastSimulationId = simId;
        f.Db.SaveChanges();

        var result = await Run(f, workflowId);

        Assert.Null(BlockedReason(result));
        Assert.Null(Error(result));
        // The readiness marker is persisted on the workflow so the promotion
        // gate can see it without re-deriving anything.
        var saved = f.Db.Workflows.Single();
        Assert.NotEqual(JsonValueKind.Undefined, saved.Metadata.ValueKind);
    }
}
