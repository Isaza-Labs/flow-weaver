using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using StepRunModel = flow_weaver_backend.Models.StepRun;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// What a `subflow` node reports about the run it started.
//
// The load-bearing one is `An_irreversible_child_keeps_the_parent_out_of_the_plan`. Before
// this, a parent run could report `rolled_back` while a child had sent an email — the same
// defect as letting an author declare `email_send` compensable, one level of nesting up and
// therefore one level harder to see.
public class SubflowOutcomeTests
{
    // ── the fixture ─────────────────────────────────────────────────────────

    private readonly ServiceProvider _sp;

    public SubflowOutcomeTests()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton<IQueueRepository>(new FakeQueue());
        services.AddLogging();
        _sp = services.BuildServiceProvider();
    }

    private AppDbContext Db() => _sp.GetRequiredService<IServiceScopeFactory>()
        .CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private WorkflowExecutor Executor() => new(
        _sp.GetRequiredService<IServiceScopeFactory>(),
        new DagParser(NullLogger<DagParser>.Instance),
        new VariableResolver(NullLogger<VariableResolver>.Instance),
        new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
        new RetryPolicyExecutor(NullLogger<RetryPolicyExecutor>.Instance),
        Microsoft.Extensions.Options.Options.Create(new WorkflowExecutorOptions
        {
            PollIntervalMs = 1,
            OrchestrationTimeoutSeconds = 5,
            WorkerEnvironment = "dev-sandbox",
        }),
        NullLogger<WorkflowExecutor>.Instance);

    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // A child run whose single step already finished with the given tier and change flag —
    // the state `PropagateChildCompletionAsync` reads on its way out.
    private (WorkflowRunModel Child, StepRunModel ParentStep) SeedFinishedChild(
        string childTier, bool childChanged, string childStatus = RunStatus.Completed)
    {
        using var db = Db();

        var snippet = new SnippetModel
        {
            SnippetId = Guid.NewGuid(), Name = "child-step", Type = "ssh",
            TargetMode = "once", Idempotency = childTier, IsActive = true,
        };
        db.Snippets.Add(snippet);

        var childNodes = "[{\"id\":\"work\",\"snippet_id\":\"" + snippet.SnippetId + "\"}]";

        var parentWf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(), Name = "parent", Nodes = J("[]"), Edges = J("[]"),
            Environment = "draft", SchemaVersion = "v1", IsActive = true,
        };
        var childWf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(), Name = "child", Nodes = J(childNodes), Edges = J("[]"),
            Environment = "draft", SchemaVersion = "v1", IsActive = true,
        };
        db.Workflows.AddRange(parentWf, childWf);

        var parentRun = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(), WorkflowId = parentWf.WorkflowId,
            Status = RunStatus.Running, InputPayload = J("{}"),
            NodesSnapshot = J("[]"), EdgesSnapshot = J("[]"), Trigger = "manual", IsActive = true,
        };
        var childRun = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(), WorkflowId = childWf.WorkflowId,
            ParentRunId = parentRun.WorkflowRunId,
            Status = RunStatus.Pending, InputPayload = J("{}"),
            NodesSnapshot = J(childNodes), EdgesSnapshot = J("[]"),
            Trigger = "subflow", IsActive = true,
        };
        db.WorkflowRuns.AddRange(parentRun, childRun);

        var parentStep = new StepRunModel
        {
            StepRunId = Guid.NewGuid(), WorkflowRunId = parentRun.WorkflowRunId,
            NodeId = "sf", ChildRunId = childRun.WorkflowRunId, Status = StepStatus.Running,
            InputPayload = J("{}"), OutputPayload = J("{}"), IsActive = true,
        };
        db.StepRuns.Add(parentStep);

        db.StepRuns.Add(new StepRunModel
        {
            StepRunId = Guid.NewGuid(), WorkflowRunId = childRun.WorkflowRunId,
            NodeId = "work", SnippetId = snippet.SnippetId,
            Status = childStatus == RunStatus.Failed ? StepStatus.Failed : StepStatus.Completed,
            ChangedState = childChanged,
            Error = childStatus == RunStatus.Failed ? "the child step failed" : string.Empty,
            StartedAt = DateTime.UtcNow,
            InputPayload = J("{}"), OutputPayload = J("""{"rtt":3}"""), IsActive = true,
        });

        db.SaveChanges();
        return (childRun, parentStep);
    }

    private StepRunModel RunChildAndReadParentStep(WorkflowRunModel child, StepRunModel parentStep)
    {
        Executor().ExecuteRunAsync(child.WorkflowRunId, default).GetAwaiter().GetResult();
        using var db = Db();
        return db.StepRuns.AsNoTracking().Single(s => s.StepRunId == parentStep.StepRunId);
    }

    // ── the tier crosses the boundary ───────────────────────────────────────

    [Fact]
    public void An_irreversible_child_keeps_the_parent_out_of_the_plan()
    {
        // The whole reason this change exists. A child that sent an email cannot be undone,
        // and a parent that listed this node as rollback-able would report `rolled_back` for
        // a run whose effects are still out there.
        var (child, step) = SeedFinishedChild("non_reversible", childChanged: true);

        var updated = RunChildAndReadParentStep(child, step);

        Assert.Equal("non_reversible", updated.Tier);
        Assert.True(updated.ChangedState);
    }

    [Fact]
    public void A_reversible_child_leaves_the_parent_reversible()
    {
        var (child, step) = SeedFinishedChild("requires_compensation", childChanged: true);

        Assert.Equal("requires_compensation", RunChildAndReadParentStep(child, step).Tier);
    }

    [Fact]
    public void A_child_in_which_nothing_ran_is_idempotent()
    {
        // Nothing ran, so there is no side effect to compensate. Scoring it any stricter
        // would keep a parent out of its own rollback plan on account of a child that did
        // nothing.
        using var db = Db();
        var parentWf = new WorkflowModel
        {
            WorkflowId = Guid.NewGuid(), Name = "parent", Nodes = J("[]"), Edges = J("[]"),
            Environment = "draft", SchemaVersion = "v1", IsActive = true,
        };
        db.Workflows.Add(parentWf);
        var parentRun = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(), WorkflowId = parentWf.WorkflowId,
            Status = RunStatus.Running, InputPayload = J("{}"),
            NodesSnapshot = J("[]"), EdgesSnapshot = J("[]"), Trigger = "manual", IsActive = true,
        };
        var childRun = new WorkflowRunModel
        {
            WorkflowRunId = Guid.NewGuid(), WorkflowId = parentWf.WorkflowId,
            ParentRunId = parentRun.WorkflowRunId, Status = RunStatus.Pending,
            InputPayload = J("{}"), NodesSnapshot = J("[]"), EdgesSnapshot = J("[]"),
            Trigger = "subflow", IsActive = true,
        };
        db.WorkflowRuns.AddRange(parentRun, childRun);
        var parentStep = new StepRunModel
        {
            StepRunId = Guid.NewGuid(), WorkflowRunId = parentRun.WorkflowRunId,
            NodeId = "sf", ChildRunId = childRun.WorkflowRunId, Status = StepStatus.Running,
            InputPayload = J("{}"), OutputPayload = J("{}"), IsActive = true,
        };
        db.StepRuns.Add(parentStep);
        db.SaveChanges();

        Assert.Equal("idempotent", RunChildAndReadParentStep(childRun, parentStep).Tier);
    }

    [Fact]
    public void A_parent_that_was_already_stricter_stays_stricter()
    {
        // Stricter-only, the same ceiling rule as everywhere else: a parent may raise what
        // the child reported, never lower it.
        var (child, step) = SeedFinishedChild("idempotent", childChanged: true);

        using (var db = Db())
        {
            db.StepRuns.Single(s => s.StepRunId == step.StepRunId).Tier = "non_reversible";
            db.SaveChanges();
        }

        Assert.Equal("non_reversible", RunChildAndReadParentStep(child, step).Tier);
    }

    // ── the change signal crosses too ───────────────────────────────────────

    [Fact]
    public void A_child_that_changed_nothing_does_not_make_the_parent_a_change()
    {
        // A subflow node has no handler; the child run IS the action. So it is the one node
        // whose change signal can only come from somewhere else.
        var (child, step) = SeedFinishedChild("idempotent", childChanged: false);

        Assert.False(RunChildAndReadParentStep(child, step).ChangedState);
    }

    // ── failures are named ──────────────────────────────────────────────────

    [Fact]
    public void A_child_that_ran_and_failed_is_subflow_failed()
    {
        // Distinct from `subflow_missing`, which is the child that never existed. The two
        // read alike and send an operator to opposite places.
        var (child, step) = SeedFinishedChild(
            "requires_compensation", childChanged: true, childStatus: RunStatus.Failed);

        var updated = RunChildAndReadParentStep(child, step);

        Assert.Equal(StepStatus.Failed, updated.Status);
        Assert.Equal("subflow_failed", updated.ErrorCode);
        // The message still names the child's own failure, so the code narrows rather than
        // replaces what a reader gets.
        Assert.Contains("the child step failed", updated.Error);
    }

    // ── the output shape ────────────────────────────────────────────────────

    [Fact]
    public void The_output_describes_the_child_run()
    {
        var (child, step) = SeedFinishedChild("idempotent", childChanged: true);

        var output = RunChildAndReadParentStep(child, step).OutputPayload;

        Assert.Equal(child.WorkflowRunId, output.GetProperty("run_id").GetGuid());
        Assert.Equal("completed", output.GetProperty("status").GetString());
        Assert.Equal("completed", output.GetProperty("final_state").GetString());
        // The child's steps are nested under `steps`, not spread at the top level. That is
        // the breaking half: a template reading the old shape has to gain `.steps`.
        Assert.Equal(3, output.GetProperty("steps").GetProperty("work").GetProperty("rtt").GetInt32());
    }
}
