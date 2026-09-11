using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using PolicyModel = flow_weaver_backend.Models.Policy;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// Phase-3 promotion gates and the per-command SSH gate.
//
// A gate is the last thing standing between a draft and production, so the two
// properties that matter are: it must not pass when its requirement is unmet
// (an over-permissive gate is worse than no gate), and when it blocks, the
// reason has to list EVERY unmet requirement — an operator who fixes the first
// miss and retries only to hit the second learns nothing.
public class PolicyGateEvaluationTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public PolicyEvaluator Build() => new(
            new PolicyEvaluatorRepository(Db),
            NullLogger<PolicyEvaluator>.Instance);

        public void SeedPolicy(string rule, string name = "gate", bool enabled = true)
        {
            Db.Policies.Add(new PolicyModel
            {
                PolicyId = Guid.NewGuid(),
                Name = name,
                Rule = TestJson.Element(rule),
                Enabled = enabled,
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void SeedRun(
            Guid workflowId, string status = RunStatus.Completed,
            DateTime? completedAt = null)
        {
            Db.WorkflowRuns.Add(new WorkflowRunModel
            {
                WorkflowRunId = Guid.NewGuid(),
                WorkflowId = workflowId,
                Status = status,
                InputPayload = TestJson.Element("{}"),
                TargetDevices = new List<Guid>(),
                TargetPools = new List<Guid>(),
                CompletedAt = completedAt ?? DateTime.UtcNow,
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static PolicyEvaluationContext Promote(
        Guid workflowId,
        string from = "qa",
        string to = "production",
        string? sshCommand = null)
        => new(
            Action: "promote",
            Environment: from,
            WorkflowName: "lldp-sync",
            WorkflowDescription: null,
            Nodes: TestJson.Element("[]"),
            DeviceRoles: Array.Empty<string>(),
            DevicePoolNames: Array.Empty<string>(),
            SshCommand: sshCommand,
            WorkflowId: workflowId,
            TargetEnvironment: to);

    private static Task<PolicyDecision> Evaluate(Fixture f, PolicyEvaluationContext ctx)
        => f.Build().EvaluateAsync(ctx, default);

    // ─── successful_runs ────────────────────────────────────────────────

    private const string RequireOneRecentRun = """
        {"action":"gate","on":"promote","from":"qa","to":"production",
         "reason":"qa validation required",
         "require":[{"type":"successful_runs","min":1,"within_days":7,"scope":"this_workflow"}]}
        """;

    [Fact]
    public async Task Gate_BlocksWhenTheWorkflowHasNoSuccessfulRuns()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);

        var decision = await Evaluate(f, Promote(Guid.NewGuid()));

        Assert.False(decision.Allowed);
        Assert.Contains("qa validation required", decision.Reason);
    }

    [Fact]
    public async Task Gate_PassesWhenTheRequirementIsMet()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId);

        Assert.True((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    // A run that succeeded long ago must not satisfy a "within 7 days" gate.
    [Fact]
    public async Task Gate_AnOldRunDoesNotSatisfyARecencyWindow()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId, completedAt: DateTime.UtcNow.AddDays(-30));

        Assert.False((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    // A failed run is not evidence of validation.
    [Fact]
    public async Task Gate_AFailedRunDoesNotCount()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId, status: RunStatus.Failed);

        Assert.False((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    // `this_workflow` scope means another workflow's green runs don't count.
    [Fact]
    public async Task Gate_AnotherWorkflowsRunsDoNotSatisfyThisWorkflowScope()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);
        f.SeedRun(Guid.NewGuid());

        Assert.False((await Evaluate(f, Promote(Guid.NewGuid()))).Allowed);
    }

    // A higher `min` needs more evidence.
    [Fact]
    public async Task Gate_TheMinimumRunCountIsHonoured()
    {
        using var f = new Fixture();
        f.SeedPolicy("""
            {"action":"gate","on":"promote",
             "require":[{"type":"successful_runs","min":3,"within_days":30,"scope":"this_workflow"}]}
            """);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId);
        f.SeedRun(workflowId);

        Assert.False((await Evaluate(f, Promote(workflowId))).Allowed);

        f.SeedRun(workflowId);
        Assert.True((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    // ─── last_successful_run_within ─────────────────────────────────────

    private const string RequireRecentRun = """
        {"action":"gate","on":"promote",
         "require":[{"type":"last_successful_run_within","days":2,"scope":"this_workflow"}]}
        """;

    [Fact]
    public async Task Gate_ARecentSuccessSatisfiesTheFreshnessRequirement()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireRecentRun);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId, completedAt: DateTime.UtcNow.AddHours(-1));

        Assert.True((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    [Fact]
    public async Task Gate_AStaleSuccessDoesNotSatisfyFreshness()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireRecentRun);
        var workflowId = Guid.NewGuid();
        f.SeedRun(workflowId, completedAt: DateTime.UtcNow.AddDays(-5));

        Assert.False((await Evaluate(f, Promote(workflowId))).Allowed);
    }

    // ─── Scoping and aggregation ────────────────────────────────────────

    // A gate declares which transition it guards; a different transition
    // must sail past it.
    [Fact]
    public async Task Gate_OnlyAppliesToTheTransitionItDeclares()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun);

        // qa → staging is not the guarded transition.
        Assert.True((await Evaluate(f, Promote(Guid.NewGuid(), to: "staging"))).Allowed);
        // draft → production is not either (the `from` doesn't match).
        Assert.True((await Evaluate(f, Promote(Guid.NewGuid(), from: "draft"))).Allowed);
    }

    [Fact]
    public async Task Gate_ADisabledGateNeverBlocks()
    {
        using var f = new Fixture();
        f.SeedPolicy(RequireOneRecentRun, enabled: false);

        Assert.True((await Evaluate(f, Promote(Guid.NewGuid()))).Allowed);
    }

    // A gate with no requirements has nothing to check, so it passes rather
    // than blocking everything.
    [Fact]
    public async Task Gate_AGateWithNoRequirementsPasses()
    {
        using var f = new Fixture();
        f.SeedPolicy("""{"action":"gate","on":"promote","reason":"nothing to check"}""");

        Assert.True((await Evaluate(f, Promote(Guid.NewGuid()))).Allowed);
    }

    // Every unmet requirement is listed, so the operator gets the full punch
    // list instead of discovering them one retry at a time.
    [Fact]
    public async Task Gate_TheReasonAggregatesEveryUnmetRequirement()
    {
        using var f = new Fixture();
        f.SeedPolicy("""
            {"action":"gate","on":"promote","reason":"not ready",
             "require":[
               {"type":"successful_runs","min":2,"within_days":7,"scope":"this_workflow"},
               {"type":"last_successful_run_within","days":1,"scope":"this_workflow"}
             ]}
            """);

        var decision = await Evaluate(f, Promote(Guid.NewGuid()));

        Assert.False(decision.Allowed);
        Assert.StartsWith("not ready:", decision.Reason);
        Assert.Contains(";", decision.Reason);
    }

    // Without an explicit reason the gate still names itself, so /admin/audit
    // shows which policy fired.
    [Fact]
    public async Task Gate_WithoutAReasonTheDecisionNamesTheGate()
    {
        using var f = new Fixture();
        f.SeedPolicy("""
            {"action":"gate","on":"promote",
             "require":[{"type":"successful_runs","min":1,"within_days":7,"scope":"this_workflow"}]}
            """, name: "qa-gate");

        var decision = await Evaluate(f, Promote(Guid.NewGuid()));

        Assert.False(decision.Allowed);
        Assert.Equal("qa-gate", decision.PolicyName);
        Assert.Contains("qa-gate", decision.Reason);
    }

    // An unrecognised requirement type is treated as UNMET, not ignored. A
    // gate exists to block, so failing closed on a requirement the backend
    // can't evaluate is the safe reading — the operator is told the type is
    // unknown rather than being waved through by a rule nobody checked.
    [Fact]
    public async Task Gate_AnUnknownRequirementTypeFailsClosed()
    {
        using var f = new Fixture();
        f.SeedPolicy("""
            {"action":"gate","on":"promote",
             "require":[{"type":"quantum_entanglement","min":1}]}
            """);

        var decision = await Evaluate(f, Promote(Guid.NewGuid()));

        Assert.False(decision.Allowed);
        Assert.Contains("unknown requirement type", decision.Reason);
    }

    // ─── ssh_command_regex ──────────────────────────────────────────────

    private const string BlockDestructive = """
        {"action":"deny","reason":"destructive command",
         "when":{"ssh_command_regex":["^reload","write erase"]}}
        """;

    // The per-command gate only fires when the caller populated SshCommand;
    // a workflow-level check must not be re-evaluated at command granularity.
    [Fact]
    public async Task SshGate_DoesNotFireWithoutACommandInContext()
    {
        using var f = new Fixture();
        f.SeedPolicy(BlockDestructive);

        Assert.True((await Evaluate(f, Promote(Guid.NewGuid()))).Allowed);
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("reload in 5")]
    [InlineData("write erase")]
    public async Task SshGate_BlocksAMatchingCommand(string command)
    {
        using var f = new Fixture();
        f.SeedPolicy(BlockDestructive);

        var decision = await Evaluate(f, Promote(Guid.NewGuid(), sshCommand: command));

        Assert.False(decision.Allowed);
        Assert.Contains("destructive command", decision.Reason);
    }

    [Theory]
    [InlineData("show version")]
    [InlineData("show reload history")]
    public async Task SshGate_LetsANonMatchingCommandThrough(string command)
    {
        using var f = new Fixture();
        f.SeedPolicy(BlockDestructive);

        Assert.True((await Evaluate(f, Promote(Guid.NewGuid(), sshCommand: command))).Allowed);
    }

    // A broken regex in a hand-written rule must not take the run down; the
    // rule simply doesn't match.
    [Fact]
    public async Task SshGate_ABrokenRegexDoesNotCrashTheEvaluation()
    {
        using var f = new Fixture();
        f.SeedPolicy("""
            {"action":"deny","reason":"x","when":{"ssh_command_regex":["[unclosed"]}}
            """);

        var decision = await Evaluate(f, Promote(Guid.NewGuid(), sshCommand: "show version"));

        Assert.True(decision.Allowed);
    }
}
