using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Phase-3 gate-rule coverage. Gates are AND-joined sets of historical
// requirements (successful_runs / last_successful_run_within /
// successful_snippet_runs); a failing gate produces a deny decision
// whose reason aggregates every unmet requirement.
public class PolicyEvaluatorGateTests
{
    private static readonly Guid WorkflowA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private static PolicyEvaluationContext PromoteCtx(string from = "qa", string to = "production") =>
        new(
            Action: "promote",
            Environment: from,
            WorkflowName: "wf",
            WorkflowDescription: null,
            Nodes: JsonDocument.Parse("[]").RootElement,
            DeviceRoles: Array.Empty<string>(),
            DevicePoolNames: Array.Empty<string>(),
            WorkflowId: WorkflowA,
            TargetEnvironment: to);

    private static async Task SeedGateAsync(AppDbContext db, string name, string ruleJson)
    {
        db.Policies.Add(new Policy
        {
            PolicyId = Guid.NewGuid(),
            Name = name,
            Rule = JsonDocument.Parse(ruleJson).RootElement,
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRunAsync(
        AppDbContext db, Guid workflowId, string status, DateTime? completedAt)
    {
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = status,
            CompletedAt = completedAt,
            StartedAt = completedAt?.AddMinutes(-1),
            CreatedAt = completedAt ?? DateTime.UtcNow,
            UpdatedAt = completedAt ?? DateTime.UtcNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedStepAsync(
        AppDbContext db, Guid snippetId, string status, DateTime? completedAt)
    {
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "n",
            SnippetId = snippetId,
            Status = status,
            CompletedAt = completedAt,
            StartedAt = completedAt?.AddSeconds(-1),
            CreatedAt = completedAt ?? DateTime.UtcNow,
            UpdatedAt = completedAt ?? DateTime.UtcNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Gate_does_not_apply_to_unrelated_action()
    {
        using var db = NewContext(nameof(Gate_does_not_apply_to_unrelated_action));
        await SeedGateAsync(db, "qa-gate",
            """{"action":"gate","on":"promote","from":"qa","to":"production","require":[{"type":"successful_runs","min":1}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        // Different action ("create") with a workflow reference — gate should
        // be silent because `on` says "promote".
        var ctx = new PolicyEvaluationContext(
            Action: "create",
            Environment: "draft",
            WorkflowName: "wf",
            WorkflowDescription: null,
            Nodes: JsonDocument.Parse("[]").RootElement,
            DeviceRoles: Array.Empty<string>(),
            DevicePoolNames: Array.Empty<string>(),
            WorkflowId: WorkflowA);
        var decision = await eval.EvaluateAsync(ctx, default);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Gate_blocks_promotion_without_recent_successful_run()
    {
        using var db = NewContext(nameof(Gate_blocks_promotion_without_recent_successful_run));
        await SeedGateAsync(db, "needs-recent-qa",
            """{"action":"gate","on":"promote","from":"qa","to":"production","reason":"qa validation required","require":[{"type":"last_successful_run_within","days":2,"scope":"this_workflow"}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.False(decision.Allowed);
        Assert.Equal("needs-recent-qa", decision.PolicyName);
        Assert.Contains("qa validation required", decision.Reason);
        Assert.Contains("no successful run", decision.Reason);
    }

    [Fact]
    public async Task Gate_passes_when_recent_run_satisfies_requirement()
    {
        using var db = NewContext(nameof(Gate_passes_when_recent_run_satisfies_requirement));
        await SeedRunAsync(db, WorkflowA, "completed", DateTime.UtcNow.AddHours(-1));
        await SeedGateAsync(db, "needs-recent-qa",
            """{"action":"gate","on":"promote","from":"qa","to":"production","require":[{"type":"last_successful_run_within","days":2,"scope":"this_workflow"}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Gate_old_run_is_not_recent_enough()
    {
        using var db = NewContext(nameof(Gate_old_run_is_not_recent_enough));
        await SeedRunAsync(db, WorkflowA, "completed", DateTime.UtcNow.AddDays(-10));
        await SeedGateAsync(db, "needs-recent-qa",
            """{"action":"gate","on":"promote","from":"qa","to":"production","require":[{"type":"last_successful_run_within","days":2}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.False(decision.Allowed);
        Assert.Contains("last successful run", decision.Reason);
    }

    [Fact]
    public async Task Gate_successful_runs_min_count()
    {
        using var db = NewContext(nameof(Gate_successful_runs_min_count));
        await SeedRunAsync(db, WorkflowA, "completed", DateTime.UtcNow.AddDays(-1));
        await SeedRunAsync(db, WorkflowA, "completed", DateTime.UtcNow.AddDays(-2));
        await SeedGateAsync(db, "needs-three",
            """{"action":"gate","on":"promote","from":"qa","to":"production","require":[{"type":"successful_runs","min":3,"within_days":7,"scope":"this_workflow"}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.False(decision.Allowed);
        Assert.Contains("got 2", decision.Reason);

        await SeedRunAsync(db, WorkflowA, "completed", DateTime.UtcNow.AddDays(-3));
        var decision2 = await eval.EvaluateAsync(PromoteCtx(), default);
        Assert.True(decision2.Allowed);
    }

    [Fact]
    public async Task Gate_to_filter_narrows_promotion_target()
    {
        using var db = NewContext(nameof(Gate_to_filter_narrows_promotion_target));
        await SeedGateAsync(db, "prod-only",
            """{"action":"gate","on":"promote","to":"production","require":[{"type":"successful_runs","min":1,"scope":"this_workflow"}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        // qa target — gate doesn't apply, allowed.
        var qaDecision = await eval.EvaluateAsync(PromoteCtx(from: "draft", to: "qa"), default);
        Assert.True(qaDecision.Allowed);

        // production target — gate applies, no runs => blocked.
        var prodDecision = await eval.EvaluateAsync(PromoteCtx(), default);
        Assert.False(prodDecision.Allowed);
    }

    [Fact]
    public async Task Gate_aggregates_multiple_unmet_requirements_into_reason()
    {
        using var db = NewContext(nameof(Gate_aggregates_multiple_unmet_requirements_into_reason));
        await SeedGateAsync(db, "strict",
            """{"action":"gate","on":"promote","from":"qa","to":"production","reason":"strict gate","require":[{"type":"successful_runs","min":2,"scope":"this_workflow"},{"type":"last_successful_run_within","days":1,"scope":"this_workflow"}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.False(decision.Allowed);
        Assert.Contains("strict gate", decision.Reason);
        Assert.Contains("got 0", decision.Reason);
        Assert.Contains("no successful run", decision.Reason);
    }

    [Fact]
    public async Task Gate_successful_snippet_runs_counts_step_runs()
    {
        using var db = NewContext(nameof(Gate_successful_snippet_runs_counts_step_runs));
        var snippet = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        for (var i = 0; i < 3; i++)
            await SeedStepAsync(db, snippet, "completed", DateTime.UtcNow.AddDays(-1));
        await SeedGateAsync(db, "needs-five",
            $$"""{"action":"gate","on":"promote","from":"qa","to":"production","require":[{"type":"successful_snippet_runs","snippet_ids":["{{snippet}}"],"min":5,"within_days":30}]}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(PromoteCtx(), default);

        Assert.False(decision.Allowed);
        Assert.Contains("got 3", decision.Reason);

        for (var i = 0; i < 2; i++)
            await SeedStepAsync(db, snippet, "completed", DateTime.UtcNow.AddDays(-2));
        var decision2 = await eval.EvaluateAsync(PromoteCtx(), default);
        Assert.True(decision2.Allowed);
    }
}
