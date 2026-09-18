using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// The reference validator's main path: every GUID a workflow node points at
// must resolve to a real, active row. A fabricated id parses
// fine and only dies at run time as an opaque "no handler for snippet type
// 'unknown'", so catching it at create time — named — is what lets the agent
// self-correct instead of shipping an un-runnable workflow.
public class WorkflowReferenceValidatorCoreTests
{

    private sealed class AllowAll : IEffectivePermissions
    {
        public Task<bool> HasAsync(string c, PermissionContext ctx, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HasAsync(string c, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public WorkflowReferenceValidator Build() => new(
            new SnippetRepository(Db),
            new IntegrationRepository(Db),
            new RepositoryBase<IntegrationAction>(Db),
            new RepositoryBase<McpServer>(Db),
            new AllowAll(),
            new FakeAppSettings(),
            new FakeUser(),
            NullLogger<WorkflowReferenceValidator>.Instance);

        public Guid SeedSnippet(string type = "ssh", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<Snippet>().Add(new Snippet
            {
                SnippetId = id,
                Name = $"snippet-{id.ToString()[..8]}",
                Type = type,
                TargetMode = "once",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedIntegration(bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "netbox",
                BaseURL = "https://netbox.example.com",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedAction(Guid integrationId, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.IntegrationActions.Add(new IntegrationAction
            {
                IntegrationActionId = id,
                IntegrationId = integrationId,
                Name = "list_devices",
                Method = "GET",
                Path = "/api/dcim/devices",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static async Task<WorkflowValidationResult> Validate(Fixture f, string nodes)
        => await f.Build().ValidateAsync(TestJson.Element(nodes), default);

    private static string Node(string id, Guid snippetId, string? configOverrides = null)
        => "{\"id\":\"" + id + "\",\"snippet_id\":\"" + snippetId + "\""
           + (configOverrides is null ? "" : ",\"config_overrides\":" + configOverrides) + "}";

    // ─── trivially valid shapes ─────────────────────────────────────────

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"not":"an array"}""")]
    [InlineData("null")]
    public async Task NonArrayOrEmptyNodesValidate(string nodes)
    {
        using var f = new Fixture();

        Assert.True((await Validate(f, nodes)).IsValid);
    }

    // Sentinels carry no GUID, so there is nothing to resolve.
    [Fact]
    public async Task SentinelNodesAreSkipped()
    {
        using var f = new Fixture();
        var nodes = """
            [{"id":"__start__","snippet_id":"__start__"},
             {"id":"__end__","snippet_id":"__end__"}]
            """;

        Assert.True((await Validate(f, nodes)).IsValid);
    }

    [Fact]
    public async Task AResolvableSnippetValidates()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet();

        Assert.True((await Validate(f, "[" + Node("n1", snippetId) + "]")).IsValid);
    }

    // ─── dangling snippet references ────────────────────────────────────

    // A GUID the agent invented parses past the placeholder check, so it has to
    // be caught by resolution.
    [Fact]
    public async Task AFabricatedSnippetIdIsRejectedByName()
    {
        using var f = new Fixture();
        var fake = Guid.NewGuid();

        var result = await Validate(f, "[" + Node("n1", fake) + "]");

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("n1", error);
        Assert.Contains(fake.ToString(), error);
        Assert.Contains("list_snippets", error);
        Assert.Contains("never invent a GUID", error);
    }

    // A soft-deleted row is just as unresolvable as a missing one.
    [Fact]
    public async Task ASoftDeletedSnippetIsDangling()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(active: false);

        Assert.False((await Validate(f, "[" + Node("n1", snippetId) + "]")).IsValid);
    }

    // The check applies to EVERY node type — a fabricated id on a report node
    // fails just as opaquely at run time as one on an integration_action.
    [Theory]
    [InlineData("report")]
    [InlineData("python_snippet")]
    [InlineData("ssh")]
    public async Task DanglingRefsAreRejectedForEveryNodeType(string type)
    {
        using var f = new Fixture();
        f.SeedSnippet(type: type);   // a real one of this type exists...

        // ...but the node points somewhere else.
        Assert.False((await Validate(f, "[" + Node("n1", Guid.NewGuid()) + "]")).IsValid);
    }

    [Fact]
    public async Task EveryDanglingNodeIsReported()
    {
        using var f = new Fixture();
        var nodes = "[" + Node("n1", Guid.NewGuid()) + "," + Node("n2", Guid.NewGuid()) + "]";

        Assert.Equal(2, (await Validate(f, nodes)).Errors.Count);
    }

    // Dangling refs are fatal on their own — a workflow whose every node points
    // at a fabricated id must not short-circuit to OK.
    [Fact]
    public async Task DanglingRefsWinOverTheIntegrationShortCircuit()
    {
        using var f = new Fixture();

        var result = await Validate(f, "[" + Node("n1", Guid.NewGuid()) + "]");

        Assert.False(result.IsValid);
        Assert.Contains("does not match any snippet", Assert.Single(result.Errors));
    }

    // ─── ordering of the gates ──────────────────────────────────────────

    // Scaffold markers are the more actionable error, so they come first even
    // when the workflow is ALSO malformed.
    [Fact]
    public async Task ScaffoldErrorsPreemptDanglingRefs()
    {
        using var f = new Fixture();
        var nodes = "[{\"id\":\"n1\",\"snippet_id\":\"" + Guid.NewGuid()
                    + "\",\"config_overrides\":{\"m\":\"draft scaffold\"}}]";

        var result = await Validate(f, nodes);

        Assert.False(result.IsValid);
        Assert.Contains("scaffold marker", Assert.Single(result.Errors));
    }

    // Placeholder ids are likewise more actionable than a resolution failure.
    [Fact]
    public async Task PlaceholderIdsPreemptOtherChecks()
    {
        using var f = new Fixture();
        var nodes = """[{"id":"n1","snippet_id":"REPLACE_SNIPPET_ID"}]""";

        var result = await Validate(f, nodes);

        Assert.False(result.IsValid);
        Assert.Contains("is not a GUID", Assert.Single(result.Errors));
    }

    // ─── integration_action nodes ───────────────────────────────────────

    private static string ActionConfig(Guid integrationId, Guid actionId)
        => "{\"integration_id\":\"" + integrationId + "\",\"action_id\":\"" + actionId + "\"}";

    [Fact]
    public async Task AWellWiredIntegrationActionValidates()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Validate(f,
            "[" + Node("n1", snippetId, ActionConfig(integrationId, actionId)) + "]");

        Assert.True(result.IsValid);
    }

    // The runtime dispatches via config_overrides, so a node without them
    // cannot run at all.
    [Fact]
    public async Task IntegrationActionWithoutConfigOverridesIsRejected()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");

        var result = await Validate(f, "[" + Node("n1", snippetId) + "]");

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("requires config_overrides", error);
        Assert.Contains("list_palette_actions", error);
    }

    [Theory]
    [InlineData("""{"action_id":"11111111-1111-1111-1111-111111111111"}""", "integration_id")]
    [InlineData("""{"integration_id":"11111111-1111-1111-1111-111111111111"}""", "action_id")]
    public async Task AMissingIdIsNamed(string config, string missingKey)
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");

        var result = await Validate(f, "[" + Node("n1", snippetId, config) + "]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(missingKey) && e.Contains("is required"));
    }

    // A human-readable name where a GUID belongs must say so — this is the
    // agent's most common mistake.
    [Fact]
    public async Task ANonGuidIdNamesTheOffendingValue()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var config = """{"integration_id":"netbox","action_id":"list_devices"}""";

        var result = await Validate(f, "[" + Node("n1", snippetId, config) + "]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must be a GUID") && e.Contains("netbox"));
    }

    [Fact]
    public async Task AnUnresolvableIntegrationIsReported()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);
        var ghost = Guid.NewGuid();

        var result = await Validate(f, "[" + Node("n1", snippetId, ActionConfig(ghost, actionId)) + "]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(ghost.ToString()) && e.Contains("not found or inactive"));
    }

    [Fact]
    public async Task AnUnresolvableActionIsReported()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var ghost = Guid.NewGuid();

        var result = await Validate(f, "[" + Node("n1", snippetId, ActionConfig(integrationId, ghost)) + "]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(ghost.ToString()) && e.Contains("not found or inactive"));
    }

    // Both ids can be real yet belong to different integrations — the runtime
    // would then call the wrong API. That mismatch is its own error.
    [Fact]
    public async Task AnActionBelongingToAnotherIntegrationIsRejected()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationA = f.SeedIntegration();
        var integrationB = f.SeedIntegration();
        var actionOfB = f.SeedAction(integrationB);

        var result = await Validate(f,
            "[" + Node("n1", snippetId, ActionConfig(integrationA, actionOfB)) + "]");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("belongs to integration"));
    }

    [Fact]
    public async Task AnInactiveIntegrationIsUnresolvable()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration(active: false);
        var actionId = f.SeedAction(integrationId);

        var result = await Validate(f,
            "[" + Node("n1", snippetId, ActionConfig(integrationId, actionId)) + "]");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task AnInactiveActionIsUnresolvable()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, active: false);

        Assert.False((await Validate(f,
            "[" + Node("n1", snippetId, ActionConfig(integrationId, actionId)) + "]")).IsValid);
    }

    // ─── mixed graphs ───────────────────────────────────────────────────

    // A non-integration node alongside a valid one must not drag the result
    // down, and every bad node is reported.
    [Fact]
    public async Task AMixedGraphReportsOnlyTheRealProblems()
    {
        using var f = new Fixture();
        var sshSnippet = f.SeedSnippet(type: "ssh");
        var actionSnippet = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var nodes = "["
            + """{"id":"__start__","snippet_id":"__start__"},"""
            + Node("n1", sshSnippet) + ","
            + Node("n2", actionSnippet, ActionConfig(integrationId, actionId)) + ","
            + """{"id":"__end__","snippet_id":"__end__"}"""
            + "]";

        Assert.True((await Validate(f, nodes)).IsValid);
    }

    [Fact]
    public async Task NonObjectNodesAreSkipped()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet();

        var nodes = "[\"a string\", 42, null, " + Node("n1", snippetId) + "]";

        Assert.True((await Validate(f, nodes)).IsValid);
    }

    // ─── the context-aware overload ─────────────────────────────────────

    // Intent warnings are advisory: the workflow still validates.
    [Fact]
    public async Task IntentMismatchIsAWarningNotAnError()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "ssh");

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId) + "]"),
            "nightly report", "emails the results to the team", default);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Warnings);
        Assert.Contains(result.Warnings!, w => w.Contains("integration_action"));
    }

    // With the right node type present there is nothing to warn about.
    [Fact]
    public async Task NoWarningWhenTheExpectedNodeTypeIsPresent()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "integration_action");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId, ActionConfig(integrationId, actionId)) + "]"),
            "nightly report", "emails the results", default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    // Several keywords pointing at the same gap collapse into one warning —
    // "email", "e-mail" and "correo" all accept the same set of node types.
    [Fact]
    public async Task DuplicateIntentKeywordsProduceOneWarning()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "ssh");

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId) + "]"),
            "email report", "sends an e-mail (correo) with the results", default);

        Assert.Single(result.Warnings!);
    }

    // …but keywords pointing at DIFFERENT gaps must not be collapsed. A
    // workflow claiming to email and to post to Slack has two things missing,
    // and naming only one of them sends the author round a second time.
    [Fact]
    public async Task DistinctIntentGapsEachProduceTheirOwnWarning()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "ssh");

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId) + "]"),
            "email report", "sends email and notifies slack", default);

        Assert.Equal(2, result.Warnings!.Count);
        Assert.Contains(result.Warnings!, w => w.Contains("email_send"));
        Assert.Contains(result.Warnings!, w => w.Contains("slack_message"));
    }

    // The native email node satisfies the email intent on its own — before
    // email_send existed, only integration_action did, and every workflow
    // using the native node warned falsely.
    [Fact]
    public async Task ANativeEmailSendNodeSatisfiesTheEmailIntent()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "email_send");

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId) + "]"),
            "nightly report", "emails the results to the team", default);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }

    [Fact]
    public async Task NoDescriptionMeansNoIntentWarnings()
    {
        using var f = new Fixture();
        var snippetId = f.SeedSnippet(type: "ssh");

        var result = await f.Build().ValidateWithContextAsync(TestJson.Element("[" + Node("n1", snippetId) + "]"), null, null, default);

        Assert.True(result.Warnings is null || result.Warnings.Count == 0);
    }
}
