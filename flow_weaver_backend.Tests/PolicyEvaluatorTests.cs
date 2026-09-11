using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Verifies the deny-matcher against a handful of representative rules.
// Uses EF Core InMemory — good enough for logic tests; if we ever add
// LINQ that the Postgres provider handles differently, promote to
// Testcontainers.Postgres.
public class PolicyEvaluatorTests
{
    private static AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private static PolicyEvaluationContext MakeContext(
        string env = "draft",
        string? snippetType = null,
        string? description = null,
        IEnumerable<string>? roles = null,
        string action = "create")
    {
        var nodes = snippetType is null
            ? JsonDocument.Parse("[]").RootElement
            : JsonDocument.Parse("[{\"id\":\"a\",\"snippet_id\":\"" + snippetType + "\"}]").RootElement;

        return new PolicyEvaluationContext(
            Action: action,
            Environment: env,
            WorkflowName: "wf",
            WorkflowDescription: description,
            Nodes: nodes,
            DeviceRoles: roles?.ToList() ?? new List<string>(),
            DevicePoolNames: Array.Empty<string>());
    }

    private static async Task SeedPolicyAsync(AppDbContext db, string name, string ruleJson, bool enabled = true)
    {
        db.Policies.Add(new flow_weaver_backend.Models.Policy
        {
            PolicyId = Guid.NewGuid(),
            Name = name,
            Description = null,
            Rule = JsonDocument.Parse(ruleJson).RootElement,
            Enabled = enabled,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task No_policies_allows_everything()
    {
        using var db = NewContext(nameof(No_policies_allows_everything));
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(MakeContext(), default);

        Assert.True(decision.Allowed);
        Assert.Null(decision.PolicyName);
    }

    [Fact]
    public async Task Env_match_blocks_the_operation()
    {
        using var db = NewContext(nameof(Env_match_blocks_the_operation));
        await SeedPolicyAsync(db, "no-prod-writes",
            """{"action":"deny","reason":"prod freeze","when":{"env":["production"]}}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(MakeContext(env: "production"), default);

        Assert.False(decision.Allowed);
        Assert.Equal("no-prod-writes", decision.PolicyName);
        Assert.Equal("prod freeze", decision.Reason);
    }

    [Fact]
    public async Task Env_mismatch_leaves_operation_allowed()
    {
        using var db = NewContext(nameof(Env_mismatch_leaves_operation_allowed));
        await SeedPolicyAsync(db, "no-prod-writes",
            """{"action":"deny","when":{"env":["production"]}}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(MakeContext(env: "draft"), default);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Description_substring_match_blocks()
    {
        using var db = NewContext(nameof(Description_substring_match_blocks));
        await SeedPolicyAsync(db, "no-bgp",
            """{"action":"deny","reason":"risky keyword","when":{"description_contains":["bgp","reload"]}}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(
            MakeContext(description: "Reset BGP neighbors on core-01"),
            default);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Disabled_policy_does_not_fire()
    {
        using var db = NewContext(nameof(Disabled_policy_does_not_fire));
        await SeedPolicyAsync(db, "paused",
            """{"action":"deny","when":{"env":["production"]}}""",
            enabled: false);
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(MakeContext(env: "production"), default);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Sentinel_snippet_id_matches_snippet_type_rule()
    {
        // The evaluator pulls `snippet_id` literals (__start__/__end__/subflow)
        // as "types" — handy for "no subflows in production" kinds of rules.
        using var db = NewContext(nameof(Sentinel_snippet_id_matches_snippet_type_rule));
        await SeedPolicyAsync(db, "no-subflows",
            """{"action":"deny","when":{"snippet_type":["subflow"]}}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(
            MakeContext(snippetType: "subflow"),
            default);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public async Task Empty_when_always_matches()
    {
        using var db = NewContext(nameof(Empty_when_always_matches));
        await SeedPolicyAsync(db, "kill-switch",
            """{"action":"deny","reason":"maintenance"}""");
        var eval = new PolicyEvaluator(new PolicyEvaluatorRepository(db), NullLogger<PolicyEvaluator>.Instance);

        var decision = await eval.EvaluateAsync(MakeContext(), default);

        Assert.False(decision.Allowed);
        Assert.Equal("maintenance", decision.Reason);
    }
}
