using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// The agent's read-and-check tools: inspecting a run, listing a device's known
// commands, and dry-running a draft workflow.
//
// The simulate tool is the interesting one — it is the agent's only way to
// find a broken graph BEFORE a run touches real devices, so a check it fails
// to make is a check nobody makes.
public class AgentRunToolTests
{

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    // ─── get_run_details ────────────────────────────────────────────────

    private sealed class RunFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public GetRunDetailsHandler Build() => new(
            new WorkflowRunRepository(Db),
            new StepRunRepository(Db),
            new FakeUser(),
            NullLogger<GetRunDetailsHandler>.Instance);

        public Guid SeedRun(
            string status = RunStatus.Completed, bool active = true, List<Guid>? devices = null)
        {
            var id = Guid.NewGuid();
            Db.WorkflowRuns.Add(new WorkflowRunModel
            {
                WorkflowRunId = id,
                WorkflowId = Guid.NewGuid(),
                Status = status,
                Trigger = "manual",
                CreatedBy = "tester",
                InputPayload = TestJson.Element("{}"),
                TargetDevices = devices ?? new List<Guid>(),
                TargetPools = new List<Guid>(),
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void SeedStep(Guid runId, string nodeId, string status, string error = "")
        {
            Db.StepRuns.Add(new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = runId,
                NodeId = nodeId,
                Status = status,
                Error = error,
                InputPayload = TestJson.Element("{}"),
                OutputPayload = TestJson.Element("{}"),
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"run_id":"not-a-guid"}""")]
    [InlineData("""{"run_id":123}""")]
    public async Task RunDetails_ABadRunIdIsAnErrorPayload(string args)
    {
        using var f = new RunFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("run_id (uuid) is required", Error(result));
    }

    [Fact]
    public async Task RunDetails_AnUnknownRunIsReportedNotThrown()
    {
        using var f = new RunFixture();

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"run_id\":\"" + Guid.NewGuid() + "\"}"), default);

        Assert.Equal("run not found", Error(result));
    }

    // A soft-deleted run is still inspectable — the agent is often asked
    // "why did that run fail" after the row was archived.
    [Fact]
    public async Task RunDetails_ASoftDeletedRunIsStillInspectable()
    {
        using var f = new RunFixture();
        var id = f.SeedRun(active: false);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"run_id\":\"" + id + "\"}"), default);

        Assert.Null(Error(result));
    }

    [Fact]
    public async Task RunDetails_ReportsTheRunAndItsSteps()
    {
        using var f = new RunFixture();
        var id = f.SeedRun(devices: new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
        f.SeedStep(id, "a", StepStatus.Completed);
        f.SeedStep(id, "b", StepStatus.Failed, "ssh timed out");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"run_id\":\"" + id + "\"}"), default);

        var run = result.GetProperty("run");
        Assert.Equal(id, run.GetProperty("workflow_run_id").GetGuid());
        Assert.Equal("manual", run.GetProperty("trigger").GetString());
        Assert.Equal(2, run.GetProperty("target_device_count").GetInt32());

        var steps = result.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("ssh timed out",
            steps.Single(s => s.GetProperty("node_id").GetString() == "b")
                 .GetProperty("error_preview").GetString());
    }

    // A pathological error blob must not blow the chat token budget.
    [Fact]
    public async Task RunDetails_TheErrorPreviewIsTruncated()
    {
        using var f = new RunFixture();
        var id = f.SeedRun();
        f.SeedStep(id, "a", StepStatus.Failed, new string('x', 5000));

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"run_id\":\"" + id + "\"}"), default);

        var preview = result.GetProperty("steps").EnumerateArray().Single()
            .GetProperty("error_preview").GetString();
        Assert.Equal(200, preview!.Length);
    }

    [Fact]
    public async Task RunDetails_AStepWithNoErrorHasANullPreview()
    {
        using var f = new RunFixture();
        var id = f.SeedRun();
        f.SeedStep(id, "a", StepStatus.Completed);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"run_id\":\"" + id + "\"}"), default);

        Assert.Equal(JsonValueKind.Null,
            result.GetProperty("steps").EnumerateArray().Single().GetProperty("error_preview").ValueKind);
    }

    // ─── list_vendor_commands ───────────────────────────────────────────

    private sealed class VendorFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public ListVendorCommandsHandler Build() => new(
            new VendorCommandRepository(Db),
            new FakeUser(),
            NullLogger<ListVendorCommandsHandler>.Instance);

        public void Seed(
            string value, string kind = "exact", string deviceType = "cisco_ios",
            string family = "cisco", bool active = true)
        {
            Db.VendorCommands.Add(new VendorCommandModel
            {
                VendorCommandId = Guid.NewGuid(),
                DeviceType = deviceType,
                VendorFamily = family,
                Kind = kind,
                Value = value,
                Source = "seed",
                IsActive = active,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"device_type":""}""")]
    [InlineData("""{"device_type":"   "}""")]
    public async Task VendorCommands_ADeviceTypeIsRequired(string args)
    {
        using var f = new VendorFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("device_type is required", Error(result));
    }

    [Fact]
    public async Task VendorCommands_SplitsExactCommandsFromPatterns()
    {
        using var f = new VendorFixture();
        f.Seed("show version");
        f.Seed("show interface .*", kind: "pattern");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"device_type":"cisco_ios"}"""), default);

        Assert.Equal("show version",
            result.GetProperty("exact").EnumerateArray().Single().GetProperty("value").GetString());
        Assert.Equal("show interface .*",
            result.GetProperty("patterns").EnumerateArray().Single().GetProperty("value").GetString());
        Assert.Equal("cisco", result.GetProperty("vendor_family").GetString());
    }

    [Theory]
    [InlineData("exact", 1, 0)]
    [InlineData("pattern", 0, 1)]
    [InlineData("all", 1, 1)]
    public async Task VendorCommands_TheKindFilterNarrowsTheListing(
        string kind, int exactCount, int patternCount)
    {
        using var f = new VendorFixture();
        f.Seed("show version");
        f.Seed("show interface .*", kind: "pattern");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"device_type\":\"cisco_ios\",\"kind\":\"" + kind + "\"}"), default);

        Assert.Equal(exactCount, result.GetProperty("exact").GetArrayLength());
        Assert.Equal(patternCount, result.GetProperty("patterns").GetArrayLength());
    }

    // An unrecognised filter falls back to "all" rather than returning
    // nothing — a typo must not look like an empty catalogue.
    [Fact]
    public async Task VendorCommands_AnUnknownKindFallsBackToAll()
    {
        using var f = new VendorFixture();
        f.Seed("show version");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"device_type":"cisco_ios","kind":"quantum"}"""), default);

        Assert.Equal("all", result.GetProperty("kind_filter").GetString());
        Assert.Equal(1, result.GetProperty("exact").GetArrayLength());
    }

    // Truncation is signalled so the agent knows to narrow the query instead
    // of assuming it has seen everything.
    [Fact]
    public async Task VendorCommands_TruncationIsSignalledToTheAgent()
    {
        using var f = new VendorFixture();
        for (var i = 0; i < 5; i++) f.Seed($"show version {i}");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"device_type":"cisco_ios","limit":2}"""), default);

        Assert.Equal(2, result.GetProperty("returned").GetInt32());
        Assert.Equal(5, result.GetProperty("total_active").GetInt32());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task VendorCommands_AFullListingIsNotMarkedTruncated()
    {
        using var f = new VendorFixture();
        f.Seed("show version");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"device_type":"cisco_ios"}"""), default);

        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 500)]
    public async Task VendorCommands_TheLimitIsClamped(int requested, int expected)
    {
        using var f = new VendorFixture();
        f.Seed("show version");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"device_type\":\"cisco_ios\",\"limit\":" + requested + "}"), default);

        Assert.Equal(expected, result.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task VendorCommands_SoftDeletedEntriesAreInvisible()
    {
        using var f = new VendorFixture();
        f.Seed("show version", active: false);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"device_type":"cisco_ios"}"""), default);

        Assert.Equal(0, result.GetProperty("total_active").GetInt32());
    }

    // ─── simulate_workflow_run ──────────────────────────────────────────

    private sealed class SimulateFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public SimulateWorkflowRunHandler Build() => new(
            new RepositoryBase<WorkflowModel>(Db),
            new SnippetRepository(Db),
            new IntegrationRepository(Db),
            new IntegrationActionRepository(Db),
            new SimulationResultRepository(Db),
            new FakeUser(),
            NullLogger<SimulateWorkflowRunHandler>.Instance);

        public Guid SeedWorkflow(
            string nodes = "[]", string edges = "[]",
            string environment = "draft", bool active = true)
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
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedSnippet(string type = "ssh")
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id, Name = "s", Type = type, IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string Node(string id, string snippetId)
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId) + ",\"x\":0,\"y\":0}";

    private static async Task<JsonElement> Simulate(SimulateFixture f, Guid workflowId)
        => await f.Build().ExecuteAsync(
            TestJson.Element("{\"workflow_id\":\"" + workflowId + "\"}"), default);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"workflow_id":"nope"}""")]
    [InlineData("""{"workflow_id":123}""")]
    public async Task Simulate_ABadWorkflowIdIsAnErrorPayload(string args)
    {
        using var f = new SimulateFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("workflow_id (uuid) is required", Error(result));
    }

    [Fact]
    public async Task Simulate_AnUnknownWorkflowIsReported()
    {
        using var f = new SimulateFixture();

        Assert.Equal("workflow not found", Error(await Simulate(f, Guid.NewGuid())));
    }

    // Promoted workflows are immutable and already passed their gate, so
    // simulating one is refused with an actionable instruction.
    [Theory]
    [InlineData("qa")]
    [InlineData("production")]
    public async Task Simulate_OnlyRunsOnDrafts(string environment)
    {
        using var f = new SimulateFixture();
        var id = f.SeedWorkflow(environment: environment);

        var error = Error(await Simulate(f, id));

        Assert.Contains("only runs on drafts", error);
        Assert.Contains("clone it to a draft", error);
    }

    [Fact]
    public async Task Simulate_AValidDraftPasses()
    {
        using var f = new SimulateFixture();
        var snippetId = f.SeedSnippet();
        var id = f.SeedWorkflow(
            "[" + Node("s", "__start__") + "," + Node("a", snippetId.ToString()) + "]",
            """[{"source":"s","target":"a","type":"success"}]""");

        var result = await Simulate(f, id);

        Assert.Null(Error(result));
        Assert.Empty(result.GetProperty("issues").EnumerateArray());
    }

    // A node pointing at a snippet that doesn't exist would fail at run time
    // on a real device — the whole point of the dry run is catching it here.
    [Fact]
    public async Task Simulate_FlagsANodeReferencingAMissingSnippet()
    {
        using var f = new SimulateFixture();
        var id = f.SeedWorkflow("[" + Node("a", Guid.NewGuid().ToString()) + "]");

        var result = await Simulate(f, id);

        Assert.NotEmpty(result.GetProperty("issues").EnumerateArray());
    }

    // Duplicate node ids make the DAG ambiguous — two nodes would answer to
    // the same `{{ steps.X }}` reference.
    [Fact]
    public async Task Simulate_FlagsDuplicateNodeIds()
    {
        using var f = new SimulateFixture();
        var snippetId = f.SeedSnippet();
        var id = f.SeedWorkflow(
            "[" + Node("a", snippetId.ToString()) + "," + Node("a", snippetId.ToString()) + "]");

        var result = await Simulate(f, id);

        Assert.NotEmpty(result.GetProperty("issues").EnumerateArray());
    }

    // An edge pointing at a node that isn't in the graph would silently
    // never fire.
    [Fact]
    public async Task Simulate_FlagsAnEdgeToAnUnknownNode()
    {
        using var f = new SimulateFixture();
        var snippetId = f.SeedSnippet();
        var id = f.SeedWorkflow(
            "[" + Node("a", snippetId.ToString()) + "]",
            """[{"source":"a","target":"ghost","type":"success"}]""");

        var result = await Simulate(f, id);

        Assert.NotEmpty(result.GetProperty("issues").EnumerateArray());
    }

    // The simulation is stamped on the workflow so the promotion gate can
    // check that a dry run happened.
    [Fact]
    public async Task Simulate_StampsTheResultOnTheWorkflow()
    {
        using var f = new SimulateFixture();
        var id = f.SeedWorkflow("[" + Node("s", "__start__") + "]");

        await Simulate(f, id);

        Assert.NotNull(f.Db.Workflows.Single().LastSimulationId);
        Assert.Single(f.Db.SimulationResults);
    }

    [Fact]
    public async Task Simulate_AnEmptyGraphIsHandledWithoutThrowing()
    {
        using var f = new SimulateFixture();
        var id = f.SeedWorkflow("[]", "[]");

        var result = await Simulate(f, id);

        Assert.Null(Error(result));
    }

    [Fact]
    public async Task Simulate_ANonArrayGraphIsHandledWithoutThrowing()
    {
        using var f = new SimulateFixture();
        var id = f.SeedWorkflow("{}", "{}");

        var result = await Simulate(f, id);

        Assert.Null(Error(result));
    }
}

