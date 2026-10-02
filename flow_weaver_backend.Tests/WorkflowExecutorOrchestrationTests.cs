using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// The orchestrator: enqueue-time guards, the polling loop that walks the DAG
// as steps go terminal, per-device fan-out, subflow spawning and the
// child→parent completion handshake.
//
// The steps never actually execute here (no worker is running), so every test
// either drives a graph made only of sentinels — which the orchestrator
// completes inline — or pre-seeds terminal step_runs and lets the loop advance
// from them. That is exactly the replay path the reclaim logic depends on, so
// it is worth pinning on its own.
public class WorkflowExecutorOrchestrationTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // ─── Fixture ────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        private readonly string _dbName = Guid.NewGuid().ToString();
        public ServiceProvider Sp { get; }
        public FakeQueue Queue { get; } = new();
        public WorkflowExecutorOptions Options { get; }

        public Fixture(Action<WorkflowExecutorOptions>? configure = null)
        {
            Options = new WorkflowExecutorOptions
            {
                // Keep the polling loop tight — the tests seed terminal
                // step_runs up front, so one or two iterations is all it
                // takes to walk the whole DAG.
                PollIntervalMs = 1,
                OrchestrationTimeoutSeconds = 5,
                WorkerEnvironment = "dev-sandbox",
            };
            configure?.Invoke(Options);

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<IQueueRepository>(Queue);
            services.AddLogging();
            Sp = services.BuildServiceProvider();
        }

        public AppDbContext NewDb() => Sp.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public WorkflowExecutor Build() => new(
            Sp.GetRequiredService<IServiceScopeFactory>(),
            new DagParser(NullLogger<DagParser>.Instance),
            new VariableResolver(NullLogger<VariableResolver>.Instance),
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<WorkflowExecutor>.Instance);

        public void Dispose() => Sp.Dispose();
    }

    // ─── Graph builders ─────────────────────────────────────────────────

    private static string Node(string id, string snippetId, string? overrides = null)
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId)
           + ",\"x\":0,\"y\":0"
           + (overrides is null ? "" : ",\"config_overrides\":" + overrides)
           + "}";

    private static string Edge(string source, string target, string type = "success", string? condition = null)
        => "{\"source\":" + JsonSerializer.Serialize(source)
           + ",\"target\":" + JsonSerializer.Serialize(target)
           + ",\"type\":\"" + type + "\""
           + (condition is null ? "" : ",\"condition\":" + JsonSerializer.Serialize(condition))
           + "}";

    // The minimal runnable graph: __start__ → __end__, both sentinels the
    // orchestrator completes inline without dispatching a job.
    private static (string Nodes, string Edges) SentinelGraph()
        => ("[" + Node("s", "__start__") + "," + Node("e", "__end__") + "]",
            "[" + Edge("s", "e") + "]");

    private static WorkflowModel SeedWorkflow(
        AppDbContext db, string nodes, string edges,
        string environment = "draft", Guid? id = null)
    {
        var workflow = new WorkflowModel
        {
            WorkflowId = id ?? Guid.NewGuid(),
            Name = "wf",
            Environment = environment,
            Version = 1,
            IsActive = true,
            Nodes = TestJson.Element(nodes),
            Edges = TestJson.Element(edges),
        };
        db.Workflows.Add(workflow);
        db.SaveChanges();
        return workflow;
    }

    private static WorkflowRunModel SeedRun(
        AppDbContext db, Guid workflowId, string nodes, string edges,
        Guid? parentRunId = null,
        List<Guid>? targetDevices = null,
        string? input = null,
        string? createdBy = null,
        bool? stopOnFailure = null)
    {
        var run = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = RunStatus.Pending,
            InputPayload = TestJson.Element(input ?? "{}"),
            NodesSnapshot = TestJson.Element(nodes),
            EdgesSnapshot = TestJson.Element(edges),
            TargetDevices = targetDevices ?? new List<Guid>(),
            TargetPools = new List<Guid>(),
            ParentRunId = parentRunId,
            Trigger = "manual",
            CreatedBy = createdBy,
            StopOnFailure = stopOnFailure,
            IsActive = true,
        };
        db.WorkflowRuns.Add(run);
        db.SaveChanges();
        return run;
    }

    private static User SeedUser(AppDbContext db, Guid id, string email, string username = "owner")
    {
        var user = new User
        {
            UserId = id,
            Username = username,
            Email = email,
            Role = "operator",
            IsActive = true,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SnippetModel SeedSnippet(
        AppDbContext db, Guid id, string type = "ssh", string targetMode = "once")
    {
        var snippet = new SnippetModel
        {
            SnippetId = id,
            Name = "step-" + type,
            Type = type,
            TargetMode = targetMode,
            IsActive = true,
        };
        db.Snippets.Add(snippet);
        db.SaveChanges();
        return snippet;
    }

    // Defaults mirror the legacy shape an unflagged device had before the
    // environment trio: reachable from draft and production, not from qa.
    private static Device SeedDevice(
        AppDbContext db, string name,
        bool qaLab = false, bool active = true,
        bool draft = true, bool production = true)
    {
        var device = new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = name,
            IpAddress = "10.0.0.1",
            Platform = "cisco_ios",
            AllowDraft = draft,
            AllowQa = qaLab,
            AllowProduction = production,
            IsActive = active,
        };
        db.Devices.Add(device);
        db.SaveChanges();
        return device;
    }

    // ─── EnqueueRunAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_WritesTheRunAndTheOrchestratorJob()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        var runId = await f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default);

        var run = db.WorkflowRuns.Single();
        Assert.Equal(runId, run.WorkflowRunId);
        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal("manual", run.Trigger);
        Assert.Equal(User.ToString(), run.CreatedBy);
        Assert.Single(f.Queue.Enqueued);
    }

    // The DAG is frozen at enqueue time so an edit to the workflow between
    // enqueue and execution cannot change what this run does.
    [Fact]
    public async Task Enqueue_SnapshotsTheDagSoLaterEditsCannotChangeTheRun()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default);

        var run = db.WorkflowRuns.Single();
        Assert.Equal(JsonValueKind.Array, run.NodesSnapshot.ValueKind);
        Assert.Equal(2, run.NodesSnapshot.GetArrayLength());
        Assert.Equal(1, run.EdgesSnapshot.GetArrayLength());
    }

    [Fact]
    public async Task Enqueue_AnUnknownWorkflowIsRejected()
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, Guid.NewGuid(), new RunWorkflowRequest(), default));

        Assert.Contains("not found", ex.Message);
    }

    // A soft-deleted workflow can't be run.
    [Fact]
    public async Task Enqueue_AnInactiveWorkflowIsNotFound()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        wf.IsActive = false;
        db.SaveChanges();

        await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default));
    }

    // The environment guard fires at enqueue time so the user gets immediate
    // feedback instead of a deferred failure in a worker.
    [Theory]
    [InlineData("production", "dev-sandbox")]
    [InlineData("production", "qa-lab")]
    [InlineData("qa", "dev-sandbox")]
    public async Task Enqueue_RefusesAWorkerInTheWrongEnvironment(string workflowEnv, string workerEnv)
    {
        using var f = new Fixture(o => o.WorkerEnvironment = workerEnv);
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: workflowEnv);

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default));

        Assert.Contains("requires worker environment", ex.Message);
        Assert.Empty(db.WorkflowRuns);
    }

    // Worker environments are a ladder: a production box is the most
    // trusted tier and may also run qa workflows, so a single deployment
    // can exercise draft → qa → production without a second worker.
    [Theory]
    [InlineData("production", "production")]
    [InlineData("qa", "qa-lab")]
    [InlineData("qa", "production")]
    public async Task Enqueue_AcceptsTheMatchingWorkerEnvironment(string workflowEnv, string workerEnv)
    {
        using var f = new Fixture(o => o.WorkerEnvironment = workerEnv);
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: workflowEnv);

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default);

        Assert.Single(db.WorkflowRuns);
    }

    // Draft workflows are exempt — they run wherever the developer is.
    [Fact]
    public async Task Enqueue_DraftWorkflowsRunOnAnyWorker()
    {
        using var f = new Fixture(o => o.WorkerEnvironment = "production");
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: "draft");

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default);

        Assert.Single(db.WorkflowRuns);
    }

    // A structurally broken DAG is rejected before any row is written —
    // a half-created run with no job would be unrecoverable.
    [Fact]
    public async Task Enqueue_ABrokenDagIsRejectedBeforeAnyWrite()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var wf = SeedWorkflow(db,
            "[" + Node("s", "__start__") + "]",
            "[" + Edge("s", "nowhere") + "]");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default));

        Assert.Empty(db.WorkflowRuns);
        Assert.Empty(f.Queue.Enqueued);
    }

    // A run with no explicit input still gets a valid empty object, not an
    // Undefined element that would blow up template resolution downstream.
    [Fact]
    public async Task Enqueue_AnAbsentInputBecomesAnEmptyObject()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default);

        Assert.Equal(JsonValueKind.Object, db.WorkflowRuns.Single().InputPayload.ValueKind);
    }

    [Fact]
    public async Task Enqueue_KeepsTheCallersInputAndTrigger()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId,
            new RunWorkflowRequest { Input = TestJson.Element("""{"site":"madrid"}""") },
            default, trigger: "schedule");

        var run = db.WorkflowRuns.Single();
        Assert.Equal("madrid", run.InputPayload.GetProperty("site").GetString());
        Assert.Equal("schedule", run.Trigger);
    }

    // FU-2 pre-flight: a workflow whose integration is still waiting for
    // credentials must not be enqueued at all.
    [Fact]
    public async Task Enqueue_RefusesAWorkflowWhoseIntegrationStillNeedsConfig()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var integrationId = Guid.NewGuid();
        db.Integrations.Add(new flow_weaver_backend.Models.Integration
        {
            IntegrationId = integrationId,
            Name = "jira",
            Type = "generic_rest",
            BaseURL = "",
            AuthConfig = TestJson.Element("{}"),
            Headers = TestJson.Element("{}"),
            HealthCheck = TestJson.Element("{}"),
            Status = IntegrationStatus.NeedsConfig,
            Enabled = true,
            IsActive = true,
        });
        db.SaveChanges();

        var wf = SeedWorkflow(db,
            "[" + Node("s", "__start__") + ","
            + Node("a", "integration_action",
                "{\"integration_id\":\"" + integrationId + "\"}") + "]",
            "[" + Edge("s", "a") + "]");

        await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId, new RunWorkflowRequest(), default));

        Assert.Empty(db.WorkflowRuns);
    }

    // ─── OrchestrateAsync ───────────────────────────────────────────────

    [Fact]
    public async Task Orchestrate_AMissingRunIsANoOp()
    {
        using var f = new Fixture();

        await f.Build().ExecuteRunAsync(Guid.NewGuid(), default);

        using var db = f.NewDb();
        Assert.Empty(db.StepRuns);
    }

    // The sentinel-only graph is the smallest complete orchestration: both
    // nodes complete inline and the run lands on `completed`.
    [Fact]
    public async Task Orchestrate_ASentinelGraphCompletesWithoutDispatchingAnyJob()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var saved = db.WorkflowRuns.Single();
        Assert.Equal(RunStatus.Completed, saved.Status);
        Assert.NotNull(saved.StartedAt);
        Assert.NotNull(saved.CompletedAt);
        // Both sentinels get a step_run so the run's timeline is complete.
        Assert.Equal(2, db.StepRuns.Count());
        Assert.All(db.StepRuns.ToList(), s => Assert.Equal(StepStatus.Completed, s.Status));
        Assert.Empty(f.Queue.Enqueued);
    }

    // A real step node dispatches a job and leaves the step pending — the
    // orchestration then times out rather than hanging forever.
    [Fact]
    public async Task Orchestrate_ARealStepIsDispatchedToTheQueue()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("s", "__start__") + "," + Node("a", snippetId.ToString()) + "]";
        var edges = "[" + Edge("s", "a") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "a");
        Assert.Equal(snippetId, step.SnippetId);
        Assert.Single(f.Queue.Enqueued);
    }

    // Replay: a re-claimed orchestration rebuilds its state from the
    // existing step_runs instead of creating a second __start__.
    [Fact]
    public async Task Orchestrate_ReplayDoesNotDuplicateAlreadyEnqueuedNodes()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);
        var afterFirst = db.StepRuns.Count();

        // Second orchestration of the same run — the reclaim path.
        db.ChangeTracker.Clear();
        var reclaimed = db.WorkflowRuns.Single();
        reclaimed.Status = RunStatus.Pending;
        db.SaveChanges();
        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(afterFirst, db.StepRuns.Count());
    }

    // A failed step propagates to the run outcome.
    [Fact]
    public async Task Orchestrate_AFailedStepFailsTheRun()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = run.WorkflowRunId,
            NodeId = "s",
            Status = StepStatus.Failed,
            Error = "handler blew up",
            InputPayload = TestJson.Element("{}"),
            OutputPayload = TestJson.Element("{}"),
            IsActive = true,
        });
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed, db.WorkflowRuns.Single().Status);
    }

    // Failure edges fire on a failed node so the compensation branch runs.
    // The source node fails inline (unresolvable template), which is the one
    // way a step reaches a terminal state without a worker — pre-seeding a
    // terminal step instead would be swallowed by replay hydration, which
    // deliberately does NOT re-fire edges for nodes it finds already done.
    [Fact]
    public async Task Orchestrate_AFailureEdgeAdvancesToTheCompensationBranch()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("ok", "__end__") + "," + Node("comp", "__end__") + "]";
        var edges = "[" + Edge("bad", "ok") + "," + Edge("bad", "comp", "failure") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var byNode = db.StepRuns.ToDictionary(s => s.NodeId, s => s.Status);
        Assert.Contains("comp", byNode.Keys);
        // `ok` sits behind a success edge out of a step that failed, so it does not run.
        //
        // This used to assert that `ok` had NO step_run at all. That was a proxy — absence
        // reads the same whether the walk skipped the node or lost it — and the run outcome
        // model replaced it with a record: every node the walk never reached now gets a
        // `skipped` row. Asserting the row and its status is strictly more than the old
        // assertion, not less.
        Assert.Equal(StepStatus.Skipped, byNode["ok"]);
    }

    // ─── stop_on_failure ────────────────────────────────────────────────
    //
    // The rule: the walk stops at the first failed node WHOSE FAILURE IS NOT CONSUMED BY A
    // `failure` EDGE. The order of those two clauses is the whole feature — a guard that
    // checks "failed and stopping" before asking the graph disables every compensation node
    // in the product, which is what happens when it is written that way.
    //
    // So the tests come in pairs: one that the stop happens, one that compensation still
    // runs. If the second kind regresses, the guard is in the wrong order, and it regresses
    // SILENTLY — the run still reports failed, it just never told anyone.

    // `always` says "fire either way", which is a statement about the edge. Only a `failure`
    // edge is the author saying what to do about the failure, so an `always` edge does not
    // buy the walk permission to continue.
    [Fact]
    public async Task Orchestrate_AnAlwaysEdgeDoesNotConsumeAFailure()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("after", "__end__") + "]";
        var edges = "[" + Edge("bad", "after", "always") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var byNode = db.StepRuns.ToDictionary(s => s.NodeId, s => s.Status);
        Assert.Equal(StepStatus.Failed, byNode["bad"]);
        Assert.Equal(StepStatus.Skipped, byNode["after"]);
    }

    // The other half, and the one that breaks quietly. A `failure` edge IS the graph saying
    // what to do, so the walk continues — and keeps going past the compensation node.
    [Fact]
    public async Task Orchestrate_AConsumedFailureLetsTheWalkContinue()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("comp", "__end__") + "," + Node("tail", "__end__") + "]";
        var edges = "[" + Edge("bad", "comp", "failure") + "," + Edge("comp", "tail") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var byNode = db.StepRuns.ToDictionary(s => s.NodeId, s => s.Status);
        Assert.Equal(StepStatus.Failed, byNode["bad"]);
        // The compensation ran, AND the walk carried on past it. A guard in the wrong order
        // would leave both of these absent while the run still reported failed.
        Assert.NotEqual(StepStatus.Skipped, byNode["comp"]);
        Assert.NotEqual(StepStatus.Skipped, byNode["tail"]);
    }

    // Opting out restores what every run did before 2026-08. It has to be asked for.
    [Fact]
    public async Task Orchestrate_StopOnFailureFalseKeepsWalkingPastTheFailure()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("after", "__end__") + "]";
        var edges = "[" + Edge("bad", "after", "always") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges, stopOnFailure: false);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var byNode = db.StepRuns.ToDictionary(s => s.NodeId, s => s.Status);
        Assert.NotEqual(StepStatus.Skipped, byNode["after"]);
    }

    // A run recorded before the flag existed reads as null, and null EXECUTES as stopping.
    // The column exists so a reader can tell "ran under the old behaviour" from "someone
    // asked for it" — a distinction that is lost the moment the default is written into
    // historical rows.
    [Fact]
    public async Task Orchestrate_ANullFlagStopsLikeTheDefault()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("after", "__end__") + "]";
        var edges = "[" + Edge("bad", "after", "always") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges, stopOnFailure: null);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(StepStatus.Skipped, db.StepRuns.Single(s => s.NodeId == "after").Status);
    }

    // A per_device node fans out into one step per device, and its aggregate result is only
    // known when EVERY one of them is terminal. So "stop the walk" and "wait for the rest of
    // the fan-out" cannot fight: the stop is decided from the group, not from the first row
    // to fail. This is the first thing that breaks if the guard is ever moved earlier.
    [Fact]
    public async Task Orchestrate_OneFailedDeviceFailsTheNodeAndStopsTheWalk()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var nodes = "[" + Node("fan", "__start__") + "," + Node("after", "__end__") + "]";
        var edges = "[" + Edge("fan", "after", "always") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        // Two devices' worth of steps for one node, already terminal: one failed, one not.
        foreach (var (status, error) in new[]
                 {
                     (StepStatus.Failed, "device A refused"),
                     (StepStatus.Completed, string.Empty),
                 })
        {
            db.StepRuns.Add(new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = run.WorkflowRunId,
                NodeId = "fan",
                DeviceId = Guid.NewGuid(),
                Status = status,
                Error = error,
                ChangedState = false,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                InputPayload = TestJson.Element("{}"),
                OutputPayload = TestJson.Element("{}"),
                IsActive = true,
            });
        }
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed, db.WorkflowRuns.Single(r => r.WorkflowRunId == run.WorkflowRunId).Status);
        Assert.Equal(StepStatus.Skipped, db.StepRuns.Single(s => s.NodeId == "after").Status);
    }

    // ─── run.* context ──────────────────────────────────────────────────

    // The canonical notify-failure node from Skills/workflows.md, wired
    // `bad ─failure→ notify`. Every field it templates must resolve, or the
    // alert step dies on the residual scan and nobody hears about the
    // original failure — which is exactly what happened while the skill
    // documented four run fields the executor never published.
    [Fact]
    public async Task Orchestrate_TheCanonicalNotifyFailureNodeResolvesEveryRunField()
    {
        using var f = new Fixture(o =>
        {
            o.OrchestrationTimeoutSeconds = 1;
            o.PublicBaseUrl = "https://flow.example.com/";
        });
        using var db = f.NewDb();
        SeedUser(db, User, "ops@example.com");
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);

        const string notifyOverrides = """
        {"body":{
          "to":"{{ run.owner_email }}",
          "subject":"FlowWeaver run {{ run.id }} failed",
          "body":"Workflow {{ run.workflow_name }} failed at step {{ run.failed_step_id }} with: {{ run.failed_step_error }} - {{ run.url }}"
        }}
        """;
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("notify", snippetId.ToString(), notifyOverrides) + "]";
        var edges = "[" + Edge("bad", "notify", "failure") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges, createdBy: User.ToString());

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var notify = db.StepRuns.Single(s => s.NodeId == "notify");
        var body = notify.InputPayload.GetProperty("body");

        Assert.Equal("ops@example.com", body.GetProperty("to").GetString());
        Assert.Equal($"FlowWeaver run {run.WorkflowRunId} failed", body.GetProperty("subject").GetString());

        var text = body.GetProperty("body").GetString()!;
        Assert.Contains("Workflow wf failed at step bad", text);
        Assert.Contains($"https://flow.example.com/runs/{run.WorkflowRunId}", text);
        // The failing step's own message is carried through, not a placeholder.
        Assert.Contains("unresolved template references", text);

        // And the alert itself survived: had any of the four fields stayed
        // literal, the executor would have pre-failed this very step.
        Assert.NotEqual(StepStatus.Failed, notify.Status);
    }

    // The failing step's error quotes the `{{ ... }}` it couldn't resolve.
    // Carried verbatim into the alert payload, the residual scan would flag
    // the notify step for a template it never wrote.
    [Fact]
    public async Task Orchestrate_AQuotedTemplateInTheFailedStepErrorDoesNotFailTheAlert()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "["
            + Node("bad", snippetId.ToString(), """{"command":"{{ steps.ghost.output.value }}"}""") + ","
            + Node("notify", snippetId.ToString(), """{"text":"{{ run.failed_step_error }}"}""") + "]";
        var edges = "[" + Edge("bad", "notify", "failure") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var notify = db.StepRuns.Single(s => s.NodeId == "notify");
        var text = notify.InputPayload.GetProperty("text").GetString()!;

        Assert.NotEqual(StepStatus.Failed, notify.Status);
        // The offending reference is still readable, just no longer a template.
        Assert.Contains("{ steps.ghost.output.value }", text);
        Assert.DoesNotContain("{{", text);
    }

    // A node reached by an `always` edge from a HEALTHY step must not claim
    // a failure that never happened — but the fields still have to resolve,
    // or the shared notify sink fails on the success path.
    [Fact]
    public async Task Orchestrate_FailedStepFieldsAreEmptyWhenThePredecessorSucceeded()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("s", "__start__") + ","
            + Node("notify", snippetId.ToString(),
                """{"step":"{{ run.failed_step_id }}","err":"{{ run.failed_step_error }}"}""") + "]";
        var edges = "[" + Edge("s", "notify", "always") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var notify = db.StepRuns.Single(s => s.NodeId == "notify");
        Assert.NotEqual(StepStatus.Failed, notify.Status);
        Assert.Equal("", notify.InputPayload.GetProperty("step").GetString());
        Assert.Equal("", notify.InputPayload.GetProperty("err").GetString());
    }

    // No PublicBaseUrl configured: the link is useless but the template
    // still resolves, so a misconfigured deployment loses the URL rather
    // than the whole notification.
    [Fact]
    public async Task Orchestrate_RunUrlFallsBackToARelativePathWithoutABaseUrl()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("s", "__start__") + ","
            + Node("n", snippetId.ToString(), """{"link":"{{ run.url }}"}""") + "]";
        var edges = "[" + Edge("s", "n") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "n");
        Assert.Equal($"/runs/{run.WorkflowRunId}", step.InputPayload.GetProperty("link").GetString());
    }

    // Runs store the starter's user id in CreatedBy. An id that matches no
    // user (a deleted account) resolves to an empty string rather than
    // leaving the template literal and failing the alert.
    [Fact]
    public async Task Orchestrate_OwnerEmailIsEmptyWhenTheStarterCannotBeResolved()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("s", "__start__") + ","
            + Node("n", snippetId.ToString(), """{"to":"{{ run.owner_email }}"}""") + "]";
        var edges = "[" + Edge("s", "n") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges, createdBy: Guid.NewGuid().ToString());

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "n");
        Assert.NotEqual(StepStatus.Failed, step.Status);
        Assert.Equal("", step.InputPayload.GetProperty("to").GetString());
    }

    // An operator cancel between poll iterations stops the walk and the
    // cancelled status survives the finalize block.
    [Fact]
    public async Task Orchestrate_AnOperatorCancelIsNotOverwrittenByTheFinalStatus()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("s", "__start__") + "," + Node("a", snippetId.ToString()) + "]";
        var edges = "[" + Edge("s", "a") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        // A second context stands in for the cancel request arriving from
        // the API while the orchestrator is polling.
        var executor = f.Build();
        var task = executor.ExecuteRunAsync(run.WorkflowRunId, default);
        using (var other = f.NewDb())
        {
            var target = other.WorkflowRuns.Single(r => r.WorkflowRunId == run.WorkflowRunId);
            target.Status = RunStatus.Cancelled;
            other.SaveChanges();
        }
        await task;

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Cancelled, db.WorkflowRuns.Single().Status);
    }

    // A step stuck past the step timeout is force-failed so the run can
    // reach a terminal state instead of hanging until the global deadline.
    [Fact]
    public async Task Orchestrate_AStuckStepIsForceFailedByTheStepTimeout()
    {
        using var f = new Fixture(o =>
        {
            o.StepTimeoutSeconds = 0;
            o.OrchestrationTimeoutSeconds = 2;
        });
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "a");
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Equal("step timed out", step.Error);
        Assert.Equal(RunStatus.Failed, db.WorkflowRuns.Single().Status);
    }

    // Legacy rows written before the snapshot columns existed fall back to
    // the live workflow definition.
    [Fact]
    public async Task Orchestrate_ARunWithoutASnapshotFallsBackToTheLiveWorkflow()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        var run = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = wf.WorkflowId,
            Status = RunStatus.Pending,
            InputPayload = TestJson.Element("{}"),
            TargetDevices = new List<Guid>(),
            TargetPools = new List<Guid>(),
            IsActive = true,
        };
        db.WorkflowRuns.Add(run);
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Completed, db.WorkflowRuns.Single().Status);
        Assert.Equal(2, db.StepRuns.Count());
    }

    // An orchestration that throws mid-flight must still leave the run in a
    // terminal state — a run stuck on `running` never shows up as finished
    // in the UI.
    [Fact]
    public async Task Orchestrate_AnInvalidNodeFailsTheRunRatherThanLeavingItRunning()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var nodes = "[" + Node("a", "not-a-guid") + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed, db.WorkflowRuns.Single().Status);
        Assert.NotNull(db.WorkflowRuns.Single().CompletedAt);
    }

    // ─── Conditional edges ──────────────────────────────────────────────

    // A conditional edge only fires when its expression evaluates true
    // against the completed step outputs. The source step is seeded as
    // still-running with a known output and force-failed by the zero step
    // timeout, so the poll loop terminalises it in-flight — the only way to
    // get a node with a non-empty output through the loop without a worker.
    [Fact]
    public async Task Orchestrate_AConditionalEdgeFiresOnlyWhenItsExpressionHolds()
    {
        using var f = new Fixture(o =>
        {
            o.StepTimeoutSeconds = 0;
            o.OrchestrationTimeoutSeconds = 3;
        });
        using var db = f.NewDb();
        var nodes = "[" + Node("s", "__start__") + "," + Node("yes", "__end__") + "," + Node("no", "__end__") + "]";
        var edges = "["
            + Edge("s", "yes", "conditional", "{{ steps.s.output.count }} > 0") + ","
            + Edge("s", "no", "conditional", "{{ steps.s.output.count }} < 0") + "]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = run.WorkflowRunId,
            NodeId = "s",
            Status = StepStatus.Running,
            InputPayload = TestJson.Element("{}"),
            OutputPayload = TestJson.Element("""{"count":3}"""),
            IsActive = true,
        });
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var byNode = db.StepRuns.ToDictionary(s => s.NodeId, s => s.Status);
        Assert.Contains("yes", byNode.Keys);
        // Same correction as the failure-edge test above: the node whose condition did not
        // hold is now RECORDED as skipped rather than simply absent.
        Assert.Equal(StepStatus.Skipped, byNode["no"]);
    }

    // ─── Per-device fan-out ─────────────────────────────────────────────

    // A per_device snippet with several targets produces one step_run per
    // device, each stamped with its own DeviceId and its own job.
    [Fact]
    public async Task Orchestrate_PerDeviceModeFansOutOneStepPerTarget()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var d1 = SeedDevice(db, "r1");
        var d2 = SeedDevice(db, "r2");
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId, targetMode: "per_device");
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { d1.DeviceId, d2.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var steps = db.StepRuns.Where(s => s.NodeId == "a").ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(
            new[] { d1.DeviceId, d2.DeviceId }.OrderBy(x => x),
            steps.Select(s => s.DeviceId!.Value).OrderBy(x => x));
        Assert.Equal(2, f.Queue.Enqueued.Count);
    }

    // `once` mode never fans out, however many targets there are.
    [Fact]
    public async Task Orchestrate_OnceModeProducesASingleStepForManyTargets()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var d1 = SeedDevice(db, "r1");
        var d2 = SeedDevice(db, "r2");
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId, targetMode: "once");
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { d1.DeviceId, d2.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Single(db.StepRuns.Where(s => s.NodeId == "a").ToList());
    }

    // Every step gets the full target list injected so handlers that do
    // their own fan-out keep working.
    [Fact]
    public async Task Orchestrate_TheResolvedTargetsAreInjectedIntoTheStepInput()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var device = SeedDevice(db, "r1");
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { device.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "a");
        var targets = step.InputPayload.GetProperty("_targets");
        Assert.Equal(device.DeviceId.ToString(), targets.EnumerateArray().Single().GetString());
    }

    // A template pointing at a step that never ran must fail the step
    // loudly instead of dispatching a job whose handler emits the literal
    // `{{ ... }}` text.
    [Fact]
    public async Task Orchestrate_AnUnresolvableTemplateFailsTheStepInsteadOfDispatchingIt()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("a", snippetId.ToString(),
            """{"command":"{{ steps.ghost.output.value }}"}""") + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "a");
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("unresolved template references", step.Error);
        Assert.Empty(f.Queue.Enqueued);
    }

    // ─── Target resolution ──────────────────────────────────────────────

    // A qa run must not reach devices that don't allow qa.
    [Fact]
    public async Task Orchestrate_QaRunsOnlyTargetQaLabDevices()
    {
        using var f = new Fixture(o =>
        {
            o.WorkerEnvironment = "qa-lab";
            o.OrchestrationTimeoutSeconds = 1;
        });
        using var db = f.NewDb();
        var prod = SeedDevice(db, "prod-r1", qaLab: false);
        var lab = SeedDevice(db, "lab-r1", qaLab: true);
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId, targetMode: "per_device");
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges, environment: "qa");
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { prod.DeviceId, lab.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = Assert.Single(db.StepRuns.Where(s => s.NodeId == "a").ToList());
        Assert.Equal(lab.DeviceId, step.DeviceId);
    }

    // Soft-deleted devices drop out of the target set. When they were the
    // only targets the run fails instead of quietly executing device-less:
    // a step with no `_targets` and an empty `{{ device.* }}` context is
    // never what the operator asked for.
    [Fact]
    public async Task Orchestrate_InactiveDevicesAreNotTargeted()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var gone = SeedDevice(db, "retired", active: false);
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { gone.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed,
            db.WorkflowRuns.Single(r => r.WorkflowRunId == run.WorkflowRunId).Status);
        Assert.Empty(db.StepRuns.Where(s => s.WorkflowRunId == run.WorkflowRunId).ToList());
    }

    // QA-lab fail-fast. The selection that works in draft resolves to
    // nothing in qa when no device carries the QA lab flag; the run must
    // say so rather than fan out to zero devices.
    [Fact]
    public async Task Orchestrate_AQaRunWhoseTargetsAreAllNonQaLabFailsTheRun()
    {
        using var f = new Fixture(o =>
        {
            o.WorkerEnvironment = "qa-lab";
            o.OrchestrationTimeoutSeconds = 1;
        });
        using var db = f.NewDb();
        var prod = SeedDevice(db, "prod-r1", qaLab: false);
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId, targetMode: "per_device");
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges, environment: "qa");
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { prod.DeviceId });

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed,
            db.WorkflowRuns.Single(r => r.WorkflowRunId == run.WorkflowRunId).Status);
        Assert.Empty(db.StepRuns.Where(s => s.WorkflowRunId == run.WorkflowRunId).ToList());
    }

    // A run with no targets at all is still legal — nothing was requested,
    // so nothing was dropped.
    [Fact]
    public async Task Orchestrate_ARunWithoutTargetsIsNotRejected()
    {
        using var f = new Fixture(o =>
        {
            o.WorkerEnvironment = "qa-lab";
            o.OrchestrationTimeoutSeconds = 1;
        });
        using var db = f.NewDb();
        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId);
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges, environment: "qa");
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var step = db.StepRuns.Single(s => s.NodeId == "a");
        Assert.Null(step.DeviceId);
    }

    // The same gate runs at enqueue time so the caller gets a 409 with an
    // actionable message instead of a run row that dies in a worker.
    [Fact]
    public async Task Enqueue_RefusesAQaRunWhoseTargetsAreAllNonQaLab()
    {
        using var f = new Fixture(o => o.WorkerEnvironment = "qa-lab");
        using var db = f.NewDb();
        var prod = SeedDevice(db, "prod-r1", qaLab: false);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: "qa");

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId,
                new RunWorkflowRequest { TargetDevices = new List<Guid> { prod.DeviceId } },
                default));

        Assert.Contains("do not allow this environment", ex.Message);
        Assert.Empty(db.WorkflowRuns);
    }

    // The trio is symmetric: a device can be restricted to production the
    // same way it can be restricted to qa, which the single legacy flag
    // could not express — draft and production applied no filter at all.
    [Fact]
    public async Task Enqueue_RefusesADraftRunAgainstAProductionOnlyDevice()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var prodOnly = SeedDevice(db, "core-r1", draft: false, qaLab: false, production: true);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);   // draft

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId,
                new RunWorkflowRequest { TargetDevices = new List<Guid> { prodOnly.DeviceId } },
                default));

        Assert.Contains("do not allow this environment", ex.Message);
        Assert.Contains("draft", ex.Message);
        Assert.Empty(db.WorkflowRuns);
    }

    // …and the same device runs fine once the workflow is in production.
    [Fact]
    public async Task Enqueue_AcceptsAProductionRunAgainstAProductionOnlyDevice()
    {
        using var f = new Fixture(o => o.WorkerEnvironment = "production");
        using var db = f.NewDb();
        var prodOnly = SeedDevice(db, "core-r1", draft: false, qaLab: false, production: true);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: "production");

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId,
            new RunWorkflowRequest { TargetDevices = new List<Guid> { prodOnly.DeviceId } },
            default);

        Assert.Single(db.WorkflowRuns);
    }

    // Every box unticked parks the device: no environment can reach it, and
    // the executor says so instead of resolving to an empty fan-out.
    [Fact]
    public async Task Enqueue_RefusesADeviceThatAllowsNoEnvironment()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var parked = SeedDevice(db, "parked-r1", draft: false, qaLab: false, production: false);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        var ex = await Assert.ThrowsAsync<WorkflowExecutorException>(() =>
            f.Build().EnqueueRunAsync(User, wf.WorkflowId,
                new RunWorkflowRequest { TargetDevices = new List<Guid> { parked.DeviceId } },
                default));

        Assert.Contains("do not allow this environment", ex.Message);
        Assert.Empty(db.WorkflowRuns);
    }

    // Draft applies no QA filter, so the very same device is runnable there.
    [Fact]
    public async Task Enqueue_ADraftRunAcceptsNonQaLabTargets()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var prod = SeedDevice(db, "prod-r1", qaLab: false);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId,
            new RunWorkflowRequest { TargetDevices = new List<Guid> { prod.DeviceId } },
            default);

        Assert.Single(db.WorkflowRuns);
    }

    // Partial rejection is by design: a mixed pool exposing only its QA
    // members to a qa run is the documented behaviour, not an error.
    [Fact]
    public async Task Enqueue_AQaRunWithAtLeastOneQaLabTargetIsAccepted()
    {
        using var f = new Fixture(o => o.WorkerEnvironment = "qa-lab");
        using var db = f.NewDb();
        var prod = SeedDevice(db, "prod-r1", qaLab: false);
        var lab = SeedDevice(db, "lab-r1", qaLab: true);
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges, environment: "qa");

        await f.Build().EnqueueRunAsync(User, wf.WorkflowId,
            new RunWorkflowRequest { TargetDevices = new List<Guid> { prod.DeviceId, lab.DeviceId } },
            default);

        Assert.Single(db.WorkflowRuns);
    }

    // Pool membership expands into the device set, deduplicated against
    // devices already targeted directly.
    [Fact]
    public async Task Orchestrate_PoolMembersExpandIntoTheTargetSet()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var d1 = SeedDevice(db, "r1");
        var d2 = SeedDevice(db, "r2");
        var pool = new DevicePool
        {
            DevicePoolId = Guid.NewGuid(),
            Name = "edge",
            StaticMembers = new List<Guid> { d1.DeviceId, d2.DeviceId },
            IsActive = true,
        };
        db.DevicePools.Add(pool);
        db.SaveChanges();

        var snippetId = Guid.NewGuid();
        SeedSnippet(db, snippetId, targetMode: "per_device");
        var nodes = "[" + Node("a", snippetId.ToString()) + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges,
            targetDevices: new List<Guid> { d1.DeviceId });
        run.TargetPools = new List<Guid> { pool.DevicePoolId };
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var steps = db.StepRuns.Where(s => s.NodeId == "a").ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal(2, steps.Select(s => s.DeviceId).Distinct().Count());
    }

    // ─── Subflows ───────────────────────────────────────────────────────

    // A subflow node spawns a child run and a parent step_run that
    // backlinks to it, so the parent's poll loop can wait on the child.
    [Fact]
    public async Task Orchestrate_ASubflowNodeSpawnsAChildRunLinkedToItsParentStep()
    {
        using var f = new Fixture(o => o.OrchestrationTimeoutSeconds = 1);
        using var db = f.NewDb();
        var childWorkflowId = Guid.NewGuid();
        var nodes = "[" + Node("sf", "subflow",
            "{\"subflow_workflow_id\":\"" + childWorkflowId + "\"}") + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        // The child has to exist. It always did in production; the orchestrator simply never
        // checked, so this test spawned a child run for a workflow that was not there and
        // passed. Naming a workflow that does not exist is now `subflow_missing`, which is
        // what the vector `executor.subflow.unresolved_child_is_subflow_missing` asserts.
        SeedWorkflow(db, "[]", "[]", id: childWorkflowId);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var child = db.WorkflowRuns.Single(r => r.ParentRunId == run.WorkflowRunId);
        Assert.Equal(childWorkflowId, child.WorkflowId);
        Assert.Equal("subflow", child.Trigger);

        var parentStep = db.StepRuns.Single(s => s.NodeId == "sf");
        Assert.Equal(child.WorkflowRunId, parentStep.ChildRunId);
    }

    // A subflow node with no target workflow id is a config error the orchestration surfaces
    // as a failed STEP, which then fails the run.
    //
    // It used to fail the run by throwing, before any step row existed — so the run reported
    // failed with nothing saying which node was at fault, and no `failure` edge could
    // compensate for a step that was never recorded. This test asserted that absence
    // (`Assert.Empty(db.StepRuns)`); it now asserts the record, which is strictly more.
    [Fact]
    public async Task Orchestrate_ASubflowWithoutATargetWorkflowFailsTheRun()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var nodes = "[" + Node("sf", "subflow") + "]";
        var edges = "[]";
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Failed, db.WorkflowRuns.Single().Status);

        var step = Assert.Single(db.StepRuns);
        Assert.Equal("sf", step.NodeId);
        Assert.Equal(StepStatus.Failed, step.Status);
        // The code, not just the message: a caller has to be able to tell "this subflow names
        // no workflow" from "the child ran and failed" without parsing prose.
        Assert.Equal("subflow_missing", step.ErrorCode);
        Assert.False(step.ChangedState);
    }

    // The child→parent handshake: when a child run finishes, the parent's
    // subflow step goes terminal so the parent's DAG can advance.
    [Fact]
    public async Task Orchestrate_AChildRunCompletionTerminalisesTheParentStep()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var parentWf = SeedWorkflow(db, nodes, edges);
        var parentRun = SeedRun(db, parentWf.WorkflowId, nodes, edges);
        var childWf = SeedWorkflow(db, nodes, edges);
        var childRun = SeedRun(db, childWf.WorkflowId, nodes, edges, parentRunId: parentRun.WorkflowRunId);

        var parentStep = new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = parentRun.WorkflowRunId,
            NodeId = "sf",
            ChildRunId = childRun.WorkflowRunId,
            Status = StepStatus.Running,
            InputPayload = TestJson.Element("{}"),
            OutputPayload = TestJson.Element("{}"),
            IsActive = true,
        };
        db.StepRuns.Add(parentStep);
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(childRun.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var updated = db.StepRuns.Single(s => s.StepRunId == parentStep.StepRunId);
        Assert.Equal(StepStatus.Completed, updated.Status);
        Assert.NotNull(updated.CompletedAt);
    }

    // A failed child surfaces its own failing step's error on the parent
    // step so the runs UI explains why the subflow blew up.
    [Fact]
    public async Task Orchestrate_AFailedChildSurfacesItsErrorOnTheParentStep()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var parentWf = SeedWorkflow(db, nodes, edges);
        var parentRun = SeedRun(db, parentWf.WorkflowId, nodes, edges);
        var childWf = SeedWorkflow(db, nodes, edges);
        var childRun = SeedRun(db, childWf.WorkflowId, nodes, edges, parentRunId: parentRun.WorkflowRunId);

        db.StepRuns.AddRange(
            new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = parentRun.WorkflowRunId,
                NodeId = "sf",
                ChildRunId = childRun.WorkflowRunId,
                Status = StepStatus.Running,
                InputPayload = TestJson.Element("{}"),
                OutputPayload = TestJson.Element("{}"),
                IsActive = true,
            },
            new StepRun
            {
                StepRunId = Guid.NewGuid(),
                WorkflowRunId = childRun.WorkflowRunId,
                NodeId = "s",
                Status = StepStatus.Failed,
                Error = "ssh timed out",
                InputPayload = TestJson.Element("{}"),
                OutputPayload = TestJson.Element("{}"),
                IsActive = true,
            });
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(childRun.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var updated = db.StepRuns.Single(s => s.NodeId == "sf");
        Assert.Equal(StepStatus.Failed, updated.Status);
        Assert.Contains("ssh timed out", updated.Error);
    }

    // An already-terminal parent step is left alone — a reclaim must not
    // overwrite a completion another orchestrator already wrote.
    [Fact]
    public async Task Orchestrate_AnAlreadyTerminalParentStepIsNotRewritten()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var parentWf = SeedWorkflow(db, nodes, edges);
        var parentRun = SeedRun(db, parentWf.WorkflowId, nodes, edges);
        var childWf = SeedWorkflow(db, nodes, edges);
        var childRun = SeedRun(db, childWf.WorkflowId, nodes, edges, parentRunId: parentRun.WorkflowRunId);

        var parentStep = new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = parentRun.WorkflowRunId,
            NodeId = "sf",
            ChildRunId = childRun.WorkflowRunId,
            Status = StepStatus.Failed,
            Error = "written by the first orchestrator",
            InputPayload = TestJson.Element("{}"),
            OutputPayload = TestJson.Element("{}"),
            IsActive = true,
        };
        db.StepRuns.Add(parentStep);
        db.SaveChanges();

        await f.Build().ExecuteRunAsync(childRun.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        var updated = db.StepRuns.Single(s => s.StepRunId == parentStep.StepRunId);
        Assert.Equal(StepStatus.Failed, updated.Status);
        Assert.Equal("written by the first orchestrator", updated.Error);
    }

    // A top-level run has no parent to notify — the propagation is a no-op
    // rather than an error.
    [Fact]
    public async Task Orchestrate_ATopLevelRunPropagatesNothing()
    {
        using var f = new Fixture();
        using var db = f.NewDb();
        var (nodes, edges) = SentinelGraph();
        var wf = SeedWorkflow(db, nodes, edges);
        var run = SeedRun(db, wf.WorkflowId, nodes, edges);

        await f.Build().ExecuteRunAsync(run.WorkflowRunId, default);

        db.ChangeTracker.Clear();
        Assert.Equal(RunStatus.Completed, db.WorkflowRuns.Single().Status);
    }
}

