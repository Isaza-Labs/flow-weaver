using flow_weaver_backend.Services.Observability;
using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using PolicyModel = flow_weaver_backend.Models.Policy;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// Four pieces of engine-adjacent logic that decide, respectively: how long a
// failed step waits, how a template resolves (and what happens when it can't),
// which policy rules apply to a run, and what an audit row records.
//
// The retry and template halves are where a silent wrong answer does the most
// damage: an unresolved template that stringifies to its own literal text puts
// `{{ steps.x.output }}` into a device command, and a backoff that ignores its
// ceiling turns a transient failure into an hours-long wait.
public class EngineLogicBatchTests
{

    // ─── RetryPolicyExecutor ────────────────────────────────────────────

    private static RetryPolicyExecutor Retry()
        => new(NullLogger<RetryPolicyExecutor>.Instance);

    private static RetryPolicy Policy(
        int maxRetries = 3, double initial = 5, string backoff = "exponential", double maxDelay = 300)
        => new()
        {
            MaxRetries = maxRetries,
            InitialDelay = initial,
            Backoff = backoff,
            MaxDelay = maxDelay,
        };

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    public void Retry_ExponentialBackoffDoublesEachAttempt(int attempt, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds, Retry().GetDelay(Policy(), attempt).TotalSeconds);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 15)]
    public void Retry_LinearBackoffGrowsByTheInitialDelay(int attempt, double expectedSeconds)
    {
        Assert.Equal(expectedSeconds,
            Retry().GetDelay(Policy(backoff: "linear"), attempt).TotalSeconds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Retry_FixedBackoffNeverGrows(int attempt)
    {
        Assert.Equal(5, Retry().GetDelay(Policy(backoff: "fixed"), attempt).TotalSeconds);
    }

    // An unknown strategy behaves like `fixed` rather than throwing — a typo
    // in a snippet's policy must not take the run down.
    [Fact]
    public void Retry_AnUnknownBackoffFallsBackToTheInitialDelay()
    {
        Assert.Equal(5, Retry().GetDelay(Policy(backoff: "fibonacci"), 4).TotalSeconds);
    }

    // The ceiling is what stops exponential growth turning a transient
    // failure into an hours-long wait.
    [Fact]
    public void Retry_TheMaxDelayCapsExponentialGrowth()
    {
        Assert.Equal(60, Retry().GetDelay(Policy(maxDelay: 60), 10).TotalSeconds);
    }

    [Theory]
    [InlineData(3, 1, true)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    [InlineData(0, 1, false)]
    public void Retry_ShouldRetryHonoursTheAttemptBudget(int maxRetries, int attempt, bool expected)
    {
        Assert.Equal(expected, Retry().ShouldRetry(Policy(maxRetries), attempt));
    }

    // A malformed or absent policy column must yield a usable zero-retry
    // policy so callers never null-check.
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"nonsense\"")]
    [InlineData("42")]
    public void Retry_ParseFallsBackToAZeroRetryPolicy(string json)
    {
        var policy = RetryPolicyExecutor.Parse(TestJson.Element(json));

        Assert.Equal(0, policy.MaxRetries);
        Assert.Equal("exponential", policy.Backoff);
    }

    [Fact]
    public void Retry_ParseReadsTheStoredShape()
    {
        var policy = RetryPolicyExecutor.Parse(TestJson.Element(
            """{"max_retries":5,"initial_delay_seconds":2,"backoff":"linear","max_delay_seconds":30}"""));

        Assert.Equal(5, policy.MaxRetries);
        Assert.Equal(2, policy.InitialDelay);
        Assert.Equal("linear", policy.Backoff);
        Assert.Equal(30, policy.MaxDelay);
    }

    [Fact]
    public void Retry_AnEmptyObjectKeepsTheDtoDefaults()
    {
        var policy = RetryPolicyExecutor.Parse(TestJson.Element("{}"));

        Assert.Equal(0, policy.MaxRetries);
        Assert.Equal(5, policy.InitialDelay);
        Assert.Equal(300, policy.MaxDelay);
    }

    [Fact]
    public void Retry_LoggingASuccessIsSafeToCall()
    {
        Retry().LogSuccess(3);
    }

    // ─── VariableResolver ───────────────────────────────────────────────

    private static VariableResolver Resolver()
        => new(NullLogger<VariableResolver>.Instance);

    private static Dictionary<string, StepResult> Steps(params (string Node, string Output)[] steps)
        => steps.ToDictionary(s => s.Node, s => new StepResult(TestJson.Element(s.Output)));

    private static JsonElement Resolve(
        string payload,
        Dictionary<string, StepResult>? steps = null,
        string? device = null,
        string? runInput = null,
        string? runContext = null)
        => Resolver().Resolve(
            TestJson.Element(payload),
            steps ?? new Dictionary<string, StepResult>(),
            device is null ? null : TestJson.Element(device),
            runInput is null ? null : TestJson.Element(runInput),
            runContext is null ? null : TestJson.Element(runContext));

    // A whole-string template keeps the referenced value's JSON TYPE — a
    // number stays a number, so a downstream `> 0` comparison still works.
    [Theory]
    [InlineData("""{"count":42}""", JsonValueKind.Number)]
    [InlineData("""{"count":true}""", JsonValueKind.True)]
    [InlineData("""{"count":[1,2]}""", JsonValueKind.Array)]
    [InlineData("""{"count":{"a":1}}""", JsonValueKind.Object)]
    [InlineData("""{"count":null}""", JsonValueKind.Null)]
    public void Variables_AWholeStringTemplatePreservesTheValueType(string output, JsonValueKind expected)
    {
        var result = Resolve(
            """{"n":"{{ steps.a.output.count }}"}""",
            Steps(("a", output)));

        Assert.Equal(expected, result.GetProperty("n").ValueKind);
    }

    // Inline mode stringifies, because the surrounding text forces a string.
    [Fact]
    public void Variables_AnInlineTemplateIsStringified()
    {
        var result = Resolve(
            """{"cmd":"set vlan {{ steps.a.output.vlan }} now"}""",
            Steps(("a", """{"vlan":42}""")));

        Assert.Equal("set vlan 42 now", result.GetProperty("cmd").GetString());
    }

    // An unresolvable reference is left as its literal text so the executor's
    // FindUnresolvedTemplates pass can spot it and fail the step — silently
    // emitting an empty string would ship a broken command to a device.
    [Fact]
    public void Variables_AnUnresolvableTemplateKeepsItsLiteralText()
    {
        var result = Resolve("""{"n":"{{ steps.ghost.output.count }}"}""");

        Assert.Equal("{{ steps.ghost.output.count }}", result.GetProperty("n").GetString());
        Assert.NotEmpty(VariableResolver.FindUnresolvedTemplates(result));
    }

    [Fact]
    public void Variables_APathMissOnAKnownStepIsAlsoLeftLiteral()
    {
        var result = Resolve(
            """{"n":"{{ steps.a.output.missing }}"}""",
            Steps(("a", """{"present":1}""")));

        Assert.Equal("{{ steps.a.output.missing }}", result.GetProperty("n").GetString());
    }

    [Fact]
    public void Variables_DeviceTemplatesResolveFromTheDeviceContext()
    {
        var result = Resolve(
            """{"host":"{{ device.ip }}","label":"on {{ device.name }}"}""",
            device: """{"ip":"10.0.0.1","name":"r1"}""");

        Assert.Equal("10.0.0.1", result.GetProperty("host").GetString());
        Assert.Equal("on r1", result.GetProperty("label").GetString());
    }

    [Fact]
    public void Variables_WithoutADeviceContextDeviceTemplatesStayLiteral()
    {
        var result = Resolve("""{"host":"{{ device.ip }}"}""");

        Assert.Equal("{{ device.ip }}", result.GetProperty("host").GetString());
    }

    // Array indexing is part of the same grammar the condition evaluator
    // uses, so both resolve identically.
    [Fact]
    public void Variables_ArrayIndexingResolves()
    {
        var result = Resolve(
            """{"first":"{{ steps.a.output.rows[0].name }}"}""",
            Steps(("a", """{"rows":[{"name":"r1"},{"name":"r2"}]}""")));

        Assert.Equal("r1", result.GetProperty("first").GetString());
    }

    [Fact]
    public void Variables_AnOutOfRangeIndexStaysLiteral()
    {
        var result = Resolve(
            """{"n":"{{ steps.a.output.rows[9] }}"}""",
            Steps(("a", """{"rows":[1]}""")));

        Assert.Equal("{{ steps.a.output.rows[9] }}", result.GetProperty("n").GetString());
    }

    // Nested objects and arrays in the payload are walked, not just the top
    // level.
    [Fact]
    public void Variables_NestedPayloadStructuresAreWalked()
    {
        var result = Resolve(
            """{"outer":{"inner":["{{ steps.a.output.v }}"]}}""",
            Steps(("a", """{"v":"resolved"}""")));

        Assert.Equal("resolved",
            result.GetProperty("outer").GetProperty("inner").EnumerateArray().Single().GetString());
    }

    // Non-string leaves pass through untouched.
    [Fact]
    public void Variables_NonStringLeavesAreCopiedVerbatim()
    {
        var result = Resolve("""{"n":42,"b":true,"z":null}""");

        Assert.Equal(42, result.GetProperty("n").GetInt32());
        Assert.True(result.GetProperty("b").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("z").ValueKind);
    }

    [Fact]
    public void Variables_AStringWithNoTemplateIsUnchanged()
    {
        var result = Resolve("""{"s":"plain text"}""");

        Assert.Equal("plain text", result.GetProperty("s").GetString());
    }

    // ─── PolicyEvaluator ────────────────────────────────────────────────

    private sealed class PolicyFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public PolicyEvaluator Build() => new(
            new PolicyEvaluatorRepository(Db),
            NullLogger<PolicyEvaluator>.Instance);

        public void SeedPolicy(string rule, bool enabled = true, string name = "p")
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

        public void Dispose() => Db.Dispose();
    }

    private static PolicyEvaluationContext Context(
        string action = "run",
        string environment = "production",
        string? description = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? pools = null)
        => new(
            Action: action,
            Environment: environment,
            WorkflowName: null,
            WorkflowDescription: description,
            Nodes: TestJson.Element("[]"),
            DeviceRoles: roles ?? Array.Empty<string>(),
            DevicePoolNames: pools ?? Array.Empty<string>());

    private static async Task<PolicyDecision> Evaluate(PolicyFixture f, PolicyEvaluationContext ctx)
        => await f.Build().EvaluateAsync(ctx, default);

    [Fact]
    public async Task Policy_NoRulesMeansAllowed()
    {
        using var f = new PolicyFixture();

        Assert.True((await Evaluate(f, Context())).Allowed);
    }

    // A rule with no `when` block always matches — that is how a blanket deny
    // is written.
    [Fact]
    public async Task Policy_ARuleWithoutAWhenBlockAlwaysMatches()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","reason":"frozen"}""");

        var decision = await Evaluate(f, Context());

        Assert.False(decision.Allowed);
        Assert.Contains("frozen", decision.Reason);
    }

    [Fact]
    public async Task Policy_ADisabledRuleIsIgnored()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","reason":"frozen"}""", enabled: false);

        Assert.True((await Evaluate(f, Context())).Allowed);
    }

    [Fact]
    public async Task Policy_TheEnvironmentGateNarrowsTheRule()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"env":["production"]},"reason":"prod frozen"}""");

        Assert.False((await Evaluate(f, Context(environment: "production"))).Allowed);
        Assert.True((await Evaluate(f, Context(environment: "draft"))).Allowed);
    }

    [Fact]
    public async Task Policy_TheActionGateNarrowsTheRule()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"action":["promote"]}}""");

        Assert.False((await Evaluate(f, Context(action: "promote"))).Allowed);
        Assert.True((await Evaluate(f, Context(action: "run"))).Allowed);
    }

    // Gate matching is case-insensitive so "Production" in a hand-written
    // rule still fires.
    [Fact]
    public async Task Policy_GateMatchingIsCaseInsensitive()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"env":["PRODUCTION"]}}""");

        Assert.False((await Evaluate(f, Context(environment: "production"))).Allowed);
    }

    // "Any-matching" list gates: one overlapping element is enough.
    [Fact]
    public async Task Policy_ADeviceRoleGateMatchesOnAnyOverlap()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"device_role":["core","edge"]}}""");

        Assert.False((await Evaluate(f, Context(roles: new[] { "access", "edge" }))).Allowed);
        Assert.True((await Evaluate(f, Context(roles: new[] { "access" }))).Allowed);
    }

    [Fact]
    public async Task Policy_ADevicePoolGateMatchesOnAnyOverlap()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"device_pool":["lab"]}}""");

        Assert.False((await Evaluate(f, Context(pools: new[] { "lab" }))).Allowed);
        Assert.True((await Evaluate(f, Context(pools: new[] { "prod" }))).Allowed);
    }

    [Fact]
    public async Task Policy_ADescriptionGateMatchesASubstringCaseInsensitively()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"description_contains":["DELETE"]}}""");

        Assert.False((await Evaluate(f, Context(description: "will delete the vlan"))).Allowed);
        Assert.True((await Evaluate(f, Context(description: "reads the vlan"))).Allowed);
    }

    // Every gate in one `when` block is an AND: all must hold.
    [Fact]
    public async Task Policy_MultipleGatesInOneRuleAreAnded()
    {
        using var f = new PolicyFixture();
        f.SeedPolicy("""{"action":"deny","when":{"env":["production"],"action":["promote"]}}""");

        Assert.False((await Evaluate(f, Context(action: "promote", environment: "production"))).Allowed);
        Assert.True((await Evaluate(f, Context(action: "promote", environment: "qa"))).Allowed);
        Assert.True((await Evaluate(f, Context(action: "run", environment: "production"))).Allowed);
    }

    // ─── AuditLogger ────────────────────────────────────────────────────

    private sealed class AuditFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public DefaultHttpContext? Http { get; set; } = new();
        public flow_weaver_backend.Services.Identity.ICurrentUser Caller { get; set; }
            = new FakeUser ();

        public AuditLogger Build()
        {
            var accessor = new HttpContextAccessor { HttpContext = Http };
            return new AuditLogger(
                new AuditEventRepository(TestScopes.Over(Db)), Caller, accessor, NullLogger<AuditLogger>.Instance);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Audit_WritesTheRowWithEntityAndRequestMetadata()
    {
        using var f = new AuditFixture();
        f.Http!.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.9");
        f.Http.Request.Headers.UserAgent = "curl/8";
        // The correlation id comes from the X-Request-Id response header that
        // CorrelationMiddleware stamps — the SAME value TraceEvent.RequestId
        // and every Serilog line carry. It used to be ctx.TraceIdentifier,
        // which is a different id, so audit rows could not be joined to the
        // logs or traces of the request that produced them.
        f.Http.Response.Headers[CorrelationMiddleware.RequestIdHeader] = "req-1";
        var entityId = Guid.NewGuid();

        await f.Build().LogAsync("workflow", entityId, "create",
            before: null, after: new { name = "wf" });

        var row = Assert.Single(f.Db.AuditLogs);
        Assert.Equal("workflow", row.EntityType);
        Assert.Equal(entityId, row.EntityId);
        Assert.Equal("create", row.Action);
        Assert.Equal("10.0.0.9", row.Ip);
        Assert.Equal("curl/8", row.UserAgent);
        Assert.Equal("req-1", row.RequestId);
        Assert.Equal("wf", row.AfterJson.GetProperty("name").GetString());
        // A null snapshot is stored as JSON null, not as an uninitialised
        // element the jsonb column can't take.
        Assert.Equal(JsonValueKind.Null, row.BeforeJson.ValueKind);
    }

    // Background work (worker, seeders) has no HTTP context — the row must
    // still be written, just without request metadata.
    [Fact]
    public async Task Audit_WorksWithoutAnHttpContext()
    {
        using var f = new AuditFixture { Http = null };

        await f.Build().LogAsync("workflow", Guid.NewGuid(), "create");

        var row = Assert.Single(f.Db.AuditLogs);
        Assert.Null(row.Ip);
        Assert.Null(row.UserAgent);
        Assert.Null(row.RequestId);
    }

    // An unauthenticated caller (the public webhook path) still gets an audit
    // row written, just without a user id attached.
    [Fact]
    public async Task Audit_AnUnauthenticatedCallerStillWritesARow()
    {
        using var f = new AuditFixture { Caller = new UnauthenticatedUser() };

        await f.Build().LogAsync("git_webhook", null, "ingest");

        var row = Assert.Single(f.Db.AuditLogs);
        Assert.Null(row.UserId);
        Assert.Null(row.EntityId);
    }

    private sealed class UnauthenticatedUser : flow_weaver_backend.Services.Identity.ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string? Username => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string>? CapabilityCeiling => null;
    }

    // ─── DependencyResolver scoring ─────────────────────────────────────

    private static MethodInfo Method(string name)
        => typeof(DependencyResolver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    private static double NameSimilarity(string a, string b)
        => (double)Method("NameSimilarity").Invoke(null, new object?[] { a, b })!;

    [Fact]
    public void Similarity_IdenticalNamesScoreOne()
    {
        Assert.Equal(1.0, NameSimilarity("notify_slack", "notify_slack"));
    }

    [Fact]
    public void Similarity_MatchingIsCaseInsensitive()
    {
        Assert.Equal(1.0, NameSimilarity("Notify_Slack", "notify_slack"));
    }

    [Fact]
    public void Similarity_UnrelatedNamesScoreLow()
    {
        Assert.True(NameSimilarity("notify_slack", "zzzzzzzzzzzz") < 0.3);
    }

    // A near-miss has to score high enough to clear the wizard's mapping
    // threshold, otherwise the user never sees the obvious candidate.
    [Fact]
    public void Similarity_ANearMissStillScoresHigh()
    {
        Assert.True(NameSimilarity("post_message", "post_messages") > 0.8);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "x")]
    [InlineData("x", "")]
    public void Similarity_EmptyInputsDoNotThrow(string a, string b)
    {
        var score = NameSimilarity(a, b);

        Assert.InRange(score, 0.0, 1.0);
    }

    // The score is symmetric — which side the candidate is on must not
    // change the ranking.
    [Fact]
    public void Similarity_IsSymmetric()
    {
        Assert.Equal(NameSimilarity("slack", "slack_notify"), NameSimilarity("slack_notify", "slack"));
    }

    // ─── DependencyResolver end-to-end ──────────────────────────────────

    private sealed class ResolverFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public DependencyResolver Build() => new(
            new SnippetRepository(Db),
            new IntegrationRepository(Db),
            new VendorCommandRepository(Db),
            new FakeUser ());

        public void SeedIntegration(string name)
        {
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = Guid.NewGuid(),
                Name = name,
                Type = "generic_rest",
                BaseURL = "https://x.test",
                AuthConfig = TestJson.Element("{}"),
                Headers = TestJson.Element("{}"),
                HealthCheck = TestJson.Element("{}"),
                Status = IntegrationStatus.Healthy,
                Enabled = true,
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public Guid SeedSnippet(string name, string type = "ssh")
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id, Name = name, Type = type, IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // A `notify_*` stub is usually meant to reach an existing Integration
    // (Mail, Slack, Jira) rather than become a bespoke snippet, so those are
    // surfaced as candidates through a keyword bridge — plain string overlap
    // would never connect "notify_failure" to "Slack".
    [Fact]
    public async Task Resolver_AnActionStubSurfacesExistingIntegrationsAsCandidates()
    {
        using var f = new ResolverFixture();
        f.SeedIntegration("Slack");

        var report = await f.Build().ResolveAsync(TestJson.Element(
            """{"nodes":[{"id":"n1","snippet_id":"notify_slack"}]}"""), default);

        var missing = Assert.Single(report.Snippets);
        Assert.Contains(missing.CandidatesForMapping, c => c.Kind == "integration" && c.Name == "Slack");
    }

    [Fact]
    public async Task Resolver_SentinelNodesAreNotReportedMissing()
    {
        using var f = new ResolverFixture();

        var report = await f.Build().ResolveAsync(TestJson.Element(
            """{"nodes":[{"id":"s","snippet_id":"__start__"},{"id":"e","snippet_id":"__end__"}]}"""),
            default);

        Assert.Empty(report.Snippets);
    }

    [Fact]
    public async Task Resolver_AResolvedGuidIsNotReportedMissing()
    {
        using var f = new ResolverFixture();
        var id = f.SeedSnippet("collect");

        var report = await f.Build().ResolveAsync(TestJson.Element(
            "{\"nodes\":[{\"id\":\"n1\",\"snippet_id\":\"" + id + "\"}]}"), default);

        Assert.Empty(report.Snippets);
    }

    // Every missing entry offers the same three actions, which is what the
    // wizard renders as buttons.
    [Fact]
    public async Task Resolver_EveryMissingSnippetOffersTheStandardActions()
    {
        using var f = new ResolverFixture();

        var report = await f.Build().ResolveAsync(TestJson.Element(
            """{"nodes":[{"id":"n1","snippet_id":"python_thing"}]}"""), default);

        var missing = Assert.Single(report.Snippets);
        Assert.Equal(new[] { "stub", "generate_with_ai", "map_to_existing" }, missing.ActionsAvailable);
        Assert.Equal("python_snippet", missing.InferredType);
    }

    // A same-typed snippet is offered as a candidate even when the names
    // don't overlap — type is a strong enough signal to be worth showing.
    [Fact]
    public async Task Resolver_SameTypedSnippetsAreOfferedAsCandidates()
    {
        using var f = new ResolverFixture();
        var id = f.SeedSnippet("completely-different", "ssh");

        var report = await f.Build().ResolveAsync(TestJson.Element(
            """{"nodes":[{"id":"n1","snippet_id":"ssh_show_version"}]}"""), default);

        var missing = Assert.Single(report.Snippets);
        Assert.Contains(missing.CandidatesForMapping, c => c.Id == id);
    }

    [Fact]
    public async Task Resolver_ANonArrayNodesBlobYieldsNothing()
    {
        using var f = new ResolverFixture();

        var report = await f.Build().ResolveAsync(TestJson.Element("""{"nodes":{}}"""), default);

        Assert.Empty(report.Snippets);
        Assert.Empty(report.Integrations);
    }
}
