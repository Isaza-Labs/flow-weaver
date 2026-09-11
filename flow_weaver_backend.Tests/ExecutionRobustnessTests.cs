using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// Traceability: NFR-002 / TC-FW-028 — "operational robustness in execution,
// audit and recovery: restart / lease / cancellation / restore do NOT duplicate
// effects". This suite pins the application-level "no duplicated effects on
// recovery" invariants that ARE deterministically unit-testable (InMemory EF +
// direct/reflection invocation, matching the repo's IntegrationReadinessGateTests
// / WorkflowExecutorDeviceInjectionTests style):
//
//   A. Restart/reclaim RESUME idempotency — when a crashed run is re-claimed and
//      re-orchestrated, HydrateOrchestratorStateAsync reconstructs which nodes
//      already ran so the poll loop RESUMES existing step_runs instead of
//      re-creating/re-firing them (WorkflowExecutor.OrchestrateAsync guards every
//      enqueue site on `enqueuedNodeIds`). This is the core "reinicio no duplica".
//   B. Duplicate-step_run COLLAPSE — if two step_runs exist for the same device
//      (double-claim / retried fan-out), AggregatePerDeviceOutput keeps only the
//      latest, so a duplicate cannot inflate the aggregated result.
//   C. Compensation GATE — the promotion-time analyzer requires side-effecting
//      (RequiresCompensation) nodes to carry a `failure` edge to a compensating
//      node, and flags NonReversible ones. This is the design defense that keeps
//      an at-least-once retry from leaving un-undoable duplicated effects.
//
// OUT OF SCOPE here (documented, NOT covered): the raw-SQL job-queue lease
// mechanics (QueueRepository.ClaimAsync / RenewLeaseAsync / ReclaimExpiredAsync
// use Postgres-only `FOR UPDATE SKIP LOCKED` + `::interval` and need a live
// Postgres), JobReclaimHostedService end-to-end, and the full container-restart
// DR drill (RPO≤5min / RTO≤60min) — those require the live drill in
// docs/ops/dr.md, not an in-process unit test.
public class ExecutionRobustnessTests
{

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    // Minimal WorkflowExecutor: only _options (for the semaphore) and _logger are
    // touched by HydrateOrchestratorStateAsync; the orchestration collaborators
    // are never reached on this path.
    private static WorkflowExecutor NewExecutor() =>
        new(scopeFactory: null!, parser: null!, resolver: null!, conditions: null!,
            retryCalc: null!,
            options: Options.Create(new WorkflowExecutorOptions()),
            logger: NullLogger<WorkflowExecutor>.Instance);

    private static readonly MethodInfo Hydrate =
        typeof(WorkflowExecutor).GetMethod("HydrateOrchestratorStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HydrateOrchestratorStateAsync not found");

    private static readonly MethodInfo Aggregate =
        typeof(WorkflowExecutor).GetMethod("AggregatePerDeviceOutput",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AggregatePerDeviceOutput not found");

    private static void SeedStep(AppDbContext db, Guid runId, string nodeId, string status,
        Guid? deviceId = null, string outputJson = "{}")
    {
        db.StepRuns.Add(new StepRunModel
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = runId,
            NodeId = nodeId,
            Status = status,
            DeviceId = deviceId,
            OutputPayload = JsonDocument.Parse(outputJson).RootElement.Clone(),
            IsActive = true,
        });
        db.SaveChanges();
    }

    private static async Task<(Dictionary<string, StepResult> completed, HashSet<string> enqueued)>
        HydrateAsync(AppDbContext db, Guid runId)
    {
        var run = new WorkflowRunModel { WorkflowRunId = runId };
        var completed = new Dictionary<string, StepResult>();
        var enqueued = new HashSet<string>();
        await (Task)Hydrate.Invoke(NewExecutor(),
            new object[] { db, run, completed, enqueued, CancellationToken.None })!;
        return (completed, enqueued);
    }

    // ── A. Restart / reclaim resume idempotency ───────────────────────────────

    [Fact]
    public async Task Reclaim_marks_a_completed_node_as_both_enqueued_and_completed()
    {
        // Simulates a crash AFTER the start node completed. On re-orchestration
        // the node must be recognised as already-run: in `enqueued` (so
        // EnqueueStepAsync won't create a second step_run) AND in `completed`
        // (so the first poll iteration won't re-fire its outgoing edges).
        using var db = NewDb(nameof(Reclaim_marks_a_completed_node_as_both_enqueued_and_completed));
        var runId = Guid.NewGuid();
        SeedStep(db, runId, "start", StepStatus.Completed, outputJson: """{"ok":true}""");

        var (completed, enqueued) = await HydrateAsync(db, runId);

        Assert.Contains("start", enqueued);        // will NOT be re-created
        Assert.True(completed.ContainsKey("start")); // will NOT re-fire edges
    }

    [Fact]
    public async Task Reclaim_marks_an_in_flight_node_enqueued_but_not_completed()
    {
        // A step that was still RUNNING when the worker died: it must be counted
        // as enqueued (no duplicate step_run) but must NOT be treated as
        // completed (its edges stay un-fired until it actually finishes).
        using var db = NewDb(nameof(Reclaim_marks_an_in_flight_node_enqueued_but_not_completed));
        var runId = Guid.NewGuid();
        SeedStep(db, runId, "step-a", StepStatus.Running);

        var (completed, enqueued) = await HydrateAsync(db, runId);

        Assert.Contains("step-a", enqueued);
        Assert.False(completed.ContainsKey("step-a"));
    }

    [Fact]
    public async Task First_boot_with_no_step_runs_hydrates_empty_state()
    {
        // No prior step_runs (fresh run, not a reclaim) → nothing is pre-seeded,
        // so the orchestrator starts the DAG from scratch exactly once.
        using var db = NewDb(nameof(First_boot_with_no_step_runs_hydrates_empty_state));

        var (completed, enqueued) = await HydrateAsync(db, Guid.NewGuid());

        Assert.Empty(enqueued);
        Assert.Empty(completed);
    }

    [Fact]
    public async Task Hydration_is_scoped_to_the_runs_own_step_runs()
    {
        // A concurrently-recovering sibling run's step_runs must not leak into
        // this run's resume state (per-run isolation on reclaim).
        using var db = NewDb(nameof(Hydration_is_scoped_to_the_runs_own_step_runs));
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        SeedStep(db, mine, "n1", StepStatus.Completed);
        SeedStep(db, other, "n2", StepStatus.Completed);

        var (completed, enqueued) = await HydrateAsync(db, mine);

        Assert.Contains("n1", enqueued);
        Assert.DoesNotContain("n2", enqueued);
        Assert.False(completed.ContainsKey("n2"));
    }

    // ── B. Duplicate step_run collapse (no double-count) ──────────────────────

    [Fact]
    public void Duplicate_step_runs_for_one_device_collapse_to_a_single_result()
    {
        // Two completed step_runs for the SAME device (as a double-claim / retried
        // fan-out would leave behind). The aggregate must count the device ONCE,
        // keeping the latest by CreatedAt — a duplicate cannot inflate success.
        var deviceId = Guid.NewGuid();
        var steps = new List<StepRunModel>
        {
            new()
            {
                StepRunId = Guid.NewGuid(), DeviceId = deviceId, Status = StepStatus.Completed,
                OutputPayload = JsonDocument.Parse("""{"gen":1}""").RootElement.Clone(),
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                StepRunId = Guid.NewGuid(), DeviceId = deviceId, Status = StepStatus.Completed,
                OutputPayload = JsonDocument.Parse("""{"gen":2}""").RootElement.Clone(),
                CreatedAt = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc),   // latest
            },
        };
        var names = new Dictionary<Guid, string> { [deviceId] = "router-1" };

        var result = (JsonElement)Aggregate.Invoke(null,
            new object?[] { steps, names, null, "node-x" })!;

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        Assert.Equal(1, result.GetProperty("success_count").GetInt32());
        Assert.Equal(1, result.GetProperty("devices").GetArrayLength());
        // Kept the LATEST generation, not the stale first claim.
        Assert.Equal(2, result.GetProperty("devices")[0].GetProperty("output").GetProperty("gen").GetInt32());
    }

    [Fact]
    public void Distinct_devices_are_not_collapsed()
    {
        // Guard rail for the dedupe: two DIFFERENT devices must both survive.
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var steps = new List<StepRunModel>
        {
            new() { StepRunId = Guid.NewGuid(), DeviceId = d1, Status = StepStatus.Completed,
                    OutputPayload = JsonDocument.Parse("{}").RootElement.Clone(),
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { StepRunId = Guid.NewGuid(), DeviceId = d2, Status = StepStatus.Failed,
                    OutputPayload = JsonDocument.Parse("{}").RootElement.Clone(),
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc) },
        };
        var names = new Dictionary<Guid, string> { [d1] = "a", [d2] = "b" };

        var result = (JsonElement)Aggregate.Invoke(null,
            new object?[] { steps, names, null, "node-y" })!;

        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Equal(1, result.GetProperty("success_count").GetInt32());
        Assert.Equal(1, result.GetProperty("failure_count").GetInt32());
    }

    // ── C. Compensation gate (destructive effects must be reversible) ─────────

    private static WorkflowModel WorkflowWith(Guid snippetId, string nodeId, bool withFailureEdge)
    {
        var nodes = $$"""[{"id":"{{nodeId}}","snippet_id":"{{snippetId}}"}]""";
        var edges = withFailureEdge
            ? $$"""[{"source":"{{nodeId}}","target":"compensate","type":"failure"}]"""
            : "[]";
        return new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(),
            Name = "wf",
            Nodes = JsonDocument.Parse(nodes).RootElement.Clone(),
            Edges = JsonDocument.Parse(edges).RootElement.Clone(),
        };
    }

    private static WorkflowRollbackAnalyzer AnalyzerWith(AppDbContext db, string snippetType, IdempotencyKind kind)
        => new(new SnippetRepository(db), new[] { (ISnippetHandler)new FakeHandler(snippetType, kind) });

    private static Guid SeedSnippet(AppDbContext db, string type)
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel { SnippetId = id, Name = type, Type = type });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task RequiresCompensation_node_without_a_failure_edge_is_flagged()
    {
        using var db = NewDb(nameof(RequiresCompensation_node_without_a_failure_edge_is_flagged));
        var snippetId = SeedSnippet(db, "rest_call");
        var wf = WorkflowWith(snippetId, "n1", withFailureEdge: false);

        var report = await AnalyzerWith(db, "rest_call", IdempotencyKind.RequiresCompensation)
            .AnalyzeAsync(wf, CancellationToken.None);

        Assert.Single(report.RequiresCompensation);       // blocks a clean rollback
        Assert.Empty(report.CompensatingFailureEdges);
        Assert.Empty(report.NonReversible);
    }

    [Fact]
    public async Task RequiresCompensation_node_with_a_failure_edge_is_satisfied()
    {
        using var db = NewDb(nameof(RequiresCompensation_node_with_a_failure_edge_is_satisfied));
        var snippetId = SeedSnippet(db, "rest_call");
        var wf = WorkflowWith(snippetId, "n1", withFailureEdge: true);

        var report = await AnalyzerWith(db, "rest_call", IdempotencyKind.RequiresCompensation)
            .AnalyzeAsync(wf, CancellationToken.None);

        Assert.Empty(report.RequiresCompensation);
        Assert.Single(report.CompensatingFailureEdges);   // has the compensating path
    }

    [Fact]
    public async Task NonReversible_node_is_flagged_even_with_a_failure_edge()
    {
        // A NonReversible effect (e.g. ssh config push) cannot be compensated by
        // an edge — it is always surfaced so a reviewer sees it before promotion.
        using var db = NewDb(nameof(NonReversible_node_is_flagged_even_with_a_failure_edge));
        var snippetId = SeedSnippet(db, "ssh");
        var wf = WorkflowWith(snippetId, "n1", withFailureEdge: true);

        var report = await AnalyzerWith(db, "ssh", IdempotencyKind.NonReversible)
            .AnalyzeAsync(wf, CancellationToken.None);

        Assert.Single(report.NonReversible);
        Assert.Empty(report.CompensatingFailureEdges);
    }

    [Fact]
    public async Task Idempotent_node_needs_no_compensation()
    {
        // Idempotent steps are safe to re-run on reclaim, so they never block
        // rollback and don't appear in any risk list.
        using var db = NewDb(nameof(Idempotent_node_needs_no_compensation));
        var snippetId = SeedSnippet(db, "ping");
        var wf = WorkflowWith(snippetId, "n1", withFailureEdge: false);

        var report = await AnalyzerWith(db, "ping", IdempotencyKind.Idempotent)
            .AnalyzeAsync(wf, CancellationToken.None);

        Assert.Empty(report.RequiresCompensation);
        Assert.Empty(report.NonReversible);
        Assert.Empty(report.CompensatingFailureEdges);
    }

    // Minimal handler double: contributes only a Type + DefaultIdempotency to the
    // analyzer's type→kind map. ExecuteAsync is never invoked by the analyzer.
    private sealed class FakeHandler : ISnippetHandler
    {
        public FakeHandler(string type, IdempotencyKind kind)
        {
            Type = type;
            DefaultIdempotency = kind;
        }

        public string Type { get; }
        public IdempotencyKind DefaultIdempotency { get; }

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
