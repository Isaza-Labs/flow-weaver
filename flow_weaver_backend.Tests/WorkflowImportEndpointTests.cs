using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AiAgentModel = flow_weaver_backend.Models.AIAgent;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// The rest of the import controller: the analyze entry point, the SSE stream,
// the on-demand snippet generator, and the two passes that turn a foreign
// translator's `(integration_id, action_name)` pair into a runnable node.
//
// The action-resolution passes matter because a foreign translator can't reach
// the DB: without them every imported integration_action node shows up in the
// editor as "Integration: Unknown / Endpoint: GET" and the runtime has nothing
// to dispatch.
public class WorkflowImportEndpointTests
{

    private sealed class TunableAppSettings : IAppSettingsService
    {
        public double Threshold { get; set; } = Services.Settings.AppSettings.Default.ImportFuzzyMatchThreshold;
        public double Gap { get; set; } = Services.Settings.AppSettings.Default.ImportFuzzyMatchGap;
        public Task<Services.Settings.AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(Services.Settings.AppSettings.Default with
            {
                ImportFuzzyMatchThreshold = Threshold,
                ImportFuzzyMatchGap = Gap,
            });
        public Task<Services.Settings.AppSettings> UpdateAsync(
            Services.Settings.AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private sealed class AllowAllPerms : IResourcePermissionService
    {
        public Task<IReadOnlyList<flow_weaver_backend.Dtos.ResourcePermissionResponse>> ListAsync(
            string t, Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<flow_weaver_backend.Dtos.ResourcePermissionResponse> GrantAsync(
            string t, Guid id, flow_weaver_backend.Dtos.GrantResourcePermissionRequest dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RevokeAsync(Guid permissionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasAtLeastAsync(string t, Guid id, string requiredRole, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ImportDraftCache Cache { get; } = new();
        public FakeUser Caller { get; } = new();
        public TunableAppSettings Settings { get; } = new();
        public FakeHttpMessageHandler Http { get; set; } =
            new(HttpStatusCode.OK, ChatResponse("""{"name":"drafted","type":"python_snippet"}"""));
        public DefaultHttpContext HttpContext { get; } = new();

        public WorkflowImportController Build()
        {
            var controller = new WorkflowImportController(
                Cache,
                new EmptyScopes(),
                Caller,
                new FakeAudit(),
                Db,
                new FakeSchemaValidator(),
                new FakeReferenceValidator(),
                new SnippetStubBuilder(),
                new AllowAllPerms(),
                Settings,
                new UnusedBundleImporter(),
                new FakePolicyEvaluator(),
                NullLogger<WorkflowImportController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = HttpContext };
            return controller;
        }

        public GenerateSnippetForImportHandler SnippetHandler() => new(
            new LlmProviderFactory(
                new AiProviderRepository(Db),
                new RepositoryBase<AiAgentModel>(Db),
                Caller,
                new FakeHttpClientFactory(Http),
                new FakeCrypto(),
                NullLoggerFactory.Instance),
            new AiProviderRepository(Db),
            Caller,
            NullLogger<GenerateSnippetForImportHandler>.Instance);

        public ImportDraft ReadyDraft(AnalysisReport report)
        {
            var draft = Cache.Create(Caller.UserId, "", Encoding.UTF8.GetBytes("{}"));
            draft.MarkAnalyzing();
            draft.MarkReady(report);
            return draft;
        }

        public void Dispose() => Db.Dispose();
    }

    // Analyze's fire-and-forget body resolves WorkflowImportPipeline from a
    // fresh scope. An empty container makes that resolution throw INSIDE the
    // detached Task.Run, which is exactly what the endpoint's contract allows
    // — the 202 has already been returned by then.
    private sealed class EmptyScopes : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        public IServiceScope CreateScope() => _provider.CreateScope();
    }

    private static string ChatResponse(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content)
           + "}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";

    private static AnalysisReport Report(string proposed, IReadOnlyList<MissingSnippet>? missing = null)
        => new()
        {
            FormatDetected = "itential",
            ProposedWorkflow = TestJson.Element(proposed),
            MissingDependencies = new DependenciesReport
            {
                Snippets = missing ?? Array.Empty<MissingSnippet>(),
            },
        };

    private static string ProblemDetail(IActionResult result)
        => Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(
            Assert.IsType<ObjectResult>(result).Value).Detail ?? "";

    private static IntegrationModel SeedIntegration(AppDbContext db, string name, Guid? id = null)
    {
        var integration = new IntegrationModel
        {
            IntegrationId = id ?? Guid.NewGuid(),
            Name = name,
            Type = "generic_rest",
            BaseURL = "https://api.test",
            AuthConfig = TestJson.Element("{}"),
            Headers = TestJson.Element("{}"),
            HealthCheck = TestJson.Element("{}"),
            Status = IntegrationStatus.Healthy,
            Enabled = true,
            IsActive = true,
        };
        db.Integrations.Add(integration);
        db.SaveChanges();
        return integration;
    }

    private static IntegrationActionModel SeedAction(
        AppDbContext db, Guid integrationId, string name,
        string method = "POST", string path = "/x", string? description = null)
    {
        var action = new IntegrationActionModel
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = name,
            Method = method,
            Path = path,
            Description = description ?? "",
            IsActive = true,
            Enabled = true,
        };
        db.IntegrationActions.Add(action);
        db.SaveChanges();
        return action;
    }

    // An integration_action node as a foreign translator emits it: the pair is
    // known, `action_id` is not.
    private static string ActionNode(string id, Guid integrationId, string actionName)
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
           + "\"config_overrides\":{\"integration_id\":\"" + integrationId + "\","
           + "\"action_name\":" + JsonSerializer.Serialize(actionName) + "}}";

    private static string Workflow(string nodes, string name = "imported")
        => "{\"name\":" + JsonSerializer.Serialize(name)
           + ",\"nodes\":[" + nodes + "],\"edges\":[]}";

    private static string[] Warnings(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        return (string[])value.GetType().GetProperty("warnings")!.GetValue(value)!;
    }

    private static JsonElement SavedNode(Fixture f, string nodeId)
        => f.Db.Workflows.Single().Nodes.EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == nodeId);

    // ─── Analyze ────────────────────────────────────────────────────────

    private static void SetBody(Fixture f, byte[] body)
    {
        f.HttpContext.Request.Body = new MemoryStream(body);
        f.HttpContext.Request.ContentLength = body.Length;
    }

    [Fact]
    public async Task Analyze_ReturnsAnImportTokenImmediately()
    {
        using var f = new Fixture();
        SetBody(f, Encoding.UTF8.GetBytes("""{"tasks":{}}"""));

        var result = await f.Build().Analyze();

        var accepted = Assert.IsType<AcceptedResult>(result);
        var value = accepted.Value!;
        var token = (Guid)value.GetType().GetProperty("import_token")!.GetValue(value)!;
        Assert.Equal("pending", value.GetType().GetProperty("status")!.GetValue(value));
        // The draft is registered under the calling user so the poll and
        // stream endpoints can find it — and nobody else's can.
        Assert.NotNull(f.Cache.Get(token, f.Caller.UserId));
        Assert.Null(f.Cache.Get(token, Guid.NewGuid()));
    }

    [Fact]
    public async Task Analyze_AnEmptyUploadIs400()
    {
        using var f = new Fixture();
        SetBody(f, Array.Empty<byte>());

        var result = await f.Build().Analyze();

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("empty upload", ProblemDetail(result));
    }

    // The 5 MiB ceiling stops a runaway upload before it is parked in the
    // in-memory draft cache.
    [Fact]
    public async Task Analyze_AnOversizedUploadIs413()
    {
        using var f = new Fixture();
        SetBody(f, new byte[5 * 1024 * 1024 + 1]);

        var result = await f.Build().Analyze();

        Assert.Equal(413, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("exceeds", ProblemDetail(result));
    }

    [Fact]
    public async Task Analyze_KeepsTheFormatHintOnTheDraft()
    {
        using var f = new Fixture();
        SetBody(f, Encoding.UTF8.GetBytes("{}"));

        var result = await f.Build().Analyze(format_hint: "n8n");

        var value = Assert.IsType<AcceptedResult>(result).Value!;
        var token = (Guid)value.GetType().GetProperty("import_token")!.GetValue(value)!;
        Assert.Equal("n8n", f.Cache.Get(token, f.Caller.UserId)!.FormatHint);
    }

    // No hint means auto-detect, stored as the empty string rather than null
    // so the pipeline's routing note logic has a stable input.
    [Fact]
    public async Task Analyze_AnAbsentHintBecomesTheEmptyString()
    {
        using var f = new Fixture();
        SetBody(f, Encoding.UTF8.GetBytes("{}"));

        var result = await f.Build().Analyze();

        var value = Assert.IsType<AcceptedResult>(result).Value!;
        var token = (Guid)value.GetType().GetProperty("import_token")!.GetValue(value)!;
        Assert.Equal("", f.Cache.Get(token, f.Caller.UserId)!.FormatHint);
    }

    // ─── Stream ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_AnUnknownTokenIs404OnTheRawResponse()
    {
        using var f = new Fixture();
        f.HttpContext.Response.Body = new MemoryStream();

        await f.Build().Stream(Guid.NewGuid(), default);

        Assert.Equal(404, f.HttpContext.Response.StatusCode);
    }

    // A late subscriber gets an immediate snapshot frame so the wizard can
    // render the current state instead of waiting for the next transition.
    [Fact]
    public async Task Stream_EmitsASnapshotAndThenClosesOnATerminalDraft()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("")));
        var body = new MemoryStream();
        f.HttpContext.Response.Body = body;

        await f.Build().Stream(draft.Token, default);

        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal("text/event-stream", f.HttpContext.Response.ContentType);
        Assert.Equal("no-cache", f.HttpContext.Response.Headers.CacheControl);
        Assert.Contains("\"type\":\"snapshot\"", text);
        Assert.Contains("\"status\":\"ready\"", text);
    }

    // The stream terminates on the ready event rather than hanging on the
    // channel forever.
    [Fact]
    public async Task Stream_StopsOnTheTerminalEvent()
    {
        using var f = new Fixture();
        var draft = f.Cache.Create(f.Caller.UserId, "", Encoding.UTF8.GetBytes("{}"));
        draft.MarkAnalyzing("Parsing upload...");
        draft.MarkFailed("boom");
        f.HttpContext.Response.Body = new MemoryStream();

        // Completes without a cancellation token — proof that the loop broke
        // on the failed event instead of awaiting more.
        await f.Build().Stream(draft.Token, default);

        var text = Encoding.UTF8.GetString(((MemoryStream)f.HttpContext.Response.Body).ToArray());
        Assert.Contains("\"status\":\"failed\"", text);
        Assert.Contains("boom", text);
    }

    // ─── GenerateSnippet ────────────────────────────────────────────────

    [Fact]
    public async Task GenerateSnippet_AnUnknownTokenIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().GenerateSnippet(
            Guid.NewGuid(), new GenerateSnippetRequest { IdInImport = "x" }, f.SnippetHandler(), default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // A draft that hasn't finished analyzing has no missing list to draft
    // against.
    [Fact]
    public async Task GenerateSnippet_ADraftWithoutAReportIs404()
    {
        using var f = new Fixture();
        var draft = f.Cache.Create(f.Caller.UserId, "", Encoding.UTF8.GetBytes("{}"));
        draft.MarkAnalyzing();

        var result = await f.Build().GenerateSnippet(
            draft.Token, new GenerateSnippetRequest { IdInImport = "x" }, f.SnippetHandler(), default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // Only ids the analysis actually reported as missing can be drafted —
    // otherwise the endpoint would be a free-form LLM proxy.
    [Fact]
    public async Task GenerateSnippet_AnIdThatIsNotMissingIsRejected()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("")));

        var result = await f.Build().GenerateSnippet(
            draft.Token, new GenerateSnippetRequest { IdInImport = "python_thing" }, f.SnippetHandler(), default);

        Assert.Contains("not in the missing snippets list", ProblemDetail(result));
    }

    [Fact]
    public async Task GenerateSnippet_ReturnsTheHandlersProposal()
    {
        using var f = new Fixture();
        f.Db.AIProviders.Add(new AIProvider
        {
            AIProviderId = Guid.NewGuid(),
            Name = "openai-prod",
            Type = "openai",
            BaseURL = "https://api.openai.test",
            EncryptedApiKey = Encoding.UTF8.GetBytes("sk-test"),
            DefaultModel = "gpt-4o",
            Enabled = true,
            IsActive = true,
        });
        f.Db.SaveChanges();
        var draft = f.ReadyDraft(Report(Workflow(""), new[]
        {
            new MissingSnippet { IdInImport = "python_thing", InferredType = "python_snippet" },
        }));

        var result = await f.Build().GenerateSnippet(
            draft.Token,
            new GenerateSnippetRequest { IdInImport = "python_thing", PromptHint = "read the LLDP table" },
            f.SnippetHandler(),
            default);

        var payload = Assert.IsType<JsonElement>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(payload.TryGetProperty("generated_snippet", out _));
    }

    // ─── Action resolution (via Commit) ─────────────────────────────────
    //
    // ResolveIntegrationActionsAsync only runs inside the commit pipeline, so
    // these drive it through the endpoint and read the persisted node back.

    private static async Task<IActionResult> CommitWorkflow(Fixture f, string nodes)
    {
        var draft = f.ReadyDraft(Report(Workflow(nodes)));
        return await f.Build().Commit(draft.Token, new CommitImportRequest(), default);
    }

    // The exact-name case: the node comes out fully hydrated so the editor
    // and the runtime both know what to dispatch.
    [Fact]
    public async Task Resolve_AnExactNameMatchHydratesTheNode()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        var action = SeedAction(f.Db, integration.IntegrationId, "post_message", "POST", "/chat.postMessage");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "post_message"));

        Assert.Empty(Warnings(result));
        var overrides = SavedNode(f, "a").GetProperty("config_overrides");
        Assert.Equal(action.IntegrationActionId.ToString(), overrides.GetProperty("action_id").GetString());
        Assert.Equal("POST", overrides.GetProperty("method").GetString());
        Assert.Equal("/chat.postMessage", overrides.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Resolve_NameMatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        SeedAction(f.Db, integration.IntegrationId, "post_message");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "POST_MESSAGE"));

        Assert.Empty(Warnings(result));
        Assert.True(SavedNode(f, "a").GetProperty("config_overrides").TryGetProperty("action_id", out _));
    }

    // Nothing close enough: the node is left untouched and the user is told
    // exactly which action under which integration failed to resolve.
    [Fact]
    public async Task Resolve_AnUnmatchedActionWarnsAndLeavesTheNodeAlone()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        SeedAction(f.Db, integration.IntegrationId, "post_message");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "delete_universe"));

        var warning = Assert.Single(Warnings(result));
        Assert.Contains("delete_universe", warning);
        Assert.Contains("Slack", warning);
        Assert.False(SavedNode(f, "a").GetProperty("config_overrides").TryGetProperty("action_id", out _));
    }

    // Two actions with the same name under one integration is a coin flip —
    // the resolver refuses to pick and says so.
    [Fact]
    public async Task Resolve_AnAmbiguousNameIsLeftForTheUser()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        SeedAction(f.Db, integration.IntegrationId, "post_message", path: "/v1");
        SeedAction(f.Db, integration.IntegrationId, "post_message", path: "/v2");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "post_message"));

        var warning = Assert.Single(Warnings(result));
        Assert.Contains("ambiguous", warning);
        Assert.False(SavedNode(f, "a").GetProperty("config_overrides").TryGetProperty("action_id", out _));
    }

    // A near-miss above the configured threshold is auto-applied, but always
    // with a warning — the user has to be able to spot a wrong guess.
    [Fact]
    public async Task Resolve_AConfidentFuzzyMatchIsAppliedWithAWarning()
    {
        using var f = new Fixture();
        f.Settings.Threshold = 0.5;
        f.Settings.Gap = 0.1;
        var integration = SeedIntegration(f.Db, "Slack");
        var action = SeedAction(f.Db, integration.IntegrationId, "post_message");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "post_messages"));

        var warning = Assert.Single(Warnings(result));
        Assert.Contains("fuzzy-matched", warning);
        Assert.Equal(action.IntegrationActionId.ToString(),
            SavedNode(f, "a").GetProperty("config_overrides").GetProperty("action_id").GetString());
    }

    // An unreachable threshold turns every fuzzy match back into a warning.
    [Fact]
    public async Task Resolve_AThresholdOfOneDisablesFuzzyMatching()
    {
        using var f = new Fixture();
        f.Settings.Threshold = 1.01;
        var integration = SeedIntegration(f.Db, "Slack");
        SeedAction(f.Db, integration.IntegrationId, "post_message");

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "post_messages"));

        Assert.Contains("not found", Assert.Single(Warnings(result)));
        Assert.False(SavedNode(f, "a").GetProperty("config_overrides").TryGetProperty("action_id", out _));
    }

    // Actions belonging to a different integration are never candidates,
    // however well the names match.
    [Fact]
    public async Task Resolve_CandidatesAreScopedToTheNodesIntegration()
    {
        using var f = new Fixture();
        var slack = SeedIntegration(f.Db, "Slack");
        var jira = SeedIntegration(f.Db, "Jira");
        SeedAction(f.Db, jira.IntegrationId, "post_message");

        var result = await CommitWorkflow(f, ActionNode("a", slack.IntegrationId, "post_message"));

        Assert.Contains("not found", Assert.Single(Warnings(result)));
    }

    // A soft-deleted action is out of the catalogue.
    [Fact]
    public async Task Resolve_InactiveActionsAreNotCandidates()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        var action = SeedAction(f.Db, integration.IntegrationId, "post_message");
        action.IsActive = false;
        f.Db.SaveChanges();

        var result = await CommitWorkflow(f, ActionNode("a", integration.IntegrationId, "post_message"));

        Assert.Contains("not found", Assert.Single(Warnings(result)));
    }

    // Nodes that aren't integration_action pass through the resolver
    // untouched.
    [Fact]
    public async Task Resolve_NonActionNodesArePassedThrough()
    {
        using var f = new Fixture();
        var snippetId = Guid.NewGuid();

        var result = await CommitWorkflow(f,
            "{\"id\":\"a\",\"snippet_id\":\"" + snippetId + "\",\"x\":1,\"y\":2}");

        Assert.Empty(Warnings(result));
        var node = SavedNode(f, "a");
        Assert.Equal(snippetId.ToString(), node.GetProperty("snippet_id").GetString());
    }

    // A node that already carries a resolved action_id is not re-resolved.
    [Fact]
    public async Task Resolve_AlreadyResolvedNodesAreLeftAlone()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        var existingActionId = Guid.NewGuid();

        var node = "{\"id\":\"a\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                 + "\"config_overrides\":{\"integration_id\":\"" + integration.IntegrationId + "\","
                 + "\"action_name\":\"post_message\",\"action_id\":\"" + existingActionId + "\"}}";
        var result = await CommitWorkflow(f, node);

        Assert.Empty(Warnings(result));
        Assert.Equal(existingActionId.ToString(),
            SavedNode(f, "a").GetProperty("config_overrides").GetProperty("action_id").GetString());
    }

    // ─── Auto-created actions on new integrations ───────────────────────

    // A freshly created needs_config integration starts with zero actions,
    // so every action name the workflow references is fabricated as a stub —
    // otherwise the user would have to hand-create each one before the
    // import is usable.
    [Fact]
    public async Task AutoCreate_FabricatesAnActionPerReferenceOnANewIntegration()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(new AnalysisReport
        {
            ProposedWorkflow = TestJson.Element(Workflow(
                "{\"id\":\"a\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                + "\"config_overrides\":{\"integration_id\":\"jira\",\"action_name\":\"create_issue\"}},"
                + "{\"id\":\"b\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                + "\"config_overrides\":{\"integration_id\":\"jira\",\"action_name\":\"get_issue\"}}")),
            MissingDependencies = new DependenciesReport
            {
                Integrations = new[] { new MissingIntegration { IdInImport = "jira" } },
            },
        });
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "create_needs_config" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        var actions = f.Db.IntegrationActions.ToList();
        Assert.Equal(2, actions.Count);
        // The verb heuristic drives the method so the stub is plausible
        // before the user touches it.
        Assert.Equal("POST", actions.Single(a => a.Name == "create_issue").Method);
        Assert.Equal("GET", actions.Single(a => a.Name == "get_issue").Method);
        Assert.Contains(Warnings(result), w => w.Contains("auto-created"));
        // Having been created, they immediately resolve on the same commit.
        Assert.True(SavedNode(f, "a").GetProperty("config_overrides").TryGetProperty("action_id", out _));
    }

    // The same action name on two nodes yields one row, not two.
    [Fact]
    public async Task AutoCreate_DeduplicatesRepeatedActionNames()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(new AnalysisReport
        {
            ProposedWorkflow = TestJson.Element(Workflow(
                "{\"id\":\"a\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                + "\"config_overrides\":{\"integration_id\":\"jira\",\"action_name\":\"create_issue\"}},"
                + "{\"id\":\"b\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                + "\"config_overrides\":{\"integration_id\":\"jira\",\"action_name\":\"CREATE_ISSUE\"}}")),
            MissingDependencies = new DependenciesReport
            {
                Integrations = new[] { new MissingIntegration { IdInImport = "jira" } },
            },
        });
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "create_needs_config" };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Single(f.Db.IntegrationActions);
    }

    // Nothing is fabricated under an integration the user mapped to an
    // existing row — its catalogue is the user's, not ours to extend.
    [Fact]
    public async Task AutoCreate_DoesNotTouchAMappedExistingIntegration()
    {
        using var f = new Fixture();
        var existing = SeedIntegration(f.Db, "Jira");
        var draft = f.ReadyDraft(new AnalysisReport
        {
            ProposedWorkflow = TestJson.Element(Workflow(
                "{\"id\":\"a\",\"snippet_id\":\"integration_action\",\"x\":0,\"y\":0,"
                + "\"config_overrides\":{\"integration_id\":\"jira\",\"action_name\":\"create_issue\"}}")),
            MissingDependencies = new DependenciesReport
            {
                Integrations = new[] { new MissingIntegration { IdInImport = "jira" } },
            },
        });
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "map", TargetId = existing.IntegrationId };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Empty(f.Db.IntegrationActions);
    }

    // ─── AI-drafted integration actions ─────────────────────────────────

    // The agent sometimes drafts an integration_action whose `code` carries
    // (integration_name, action_name). That must materialise a real
    // Integration + IntegrationAction, never a Snippet row with a
    // "REPLACE_WITH_INTEGRATION_ID" placeholder in it.
    [Fact]
    public async Task Generated_AnIntegrationActionDraftMaterialisesIntegrationAndAction()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element(
                """{"name":"notify_slack","type":"integration_action","code":{"integration_name":"Slack","action_name":"post_message","method":"post"}}"""),
        };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(f.Db.Snippets);
        var integration = Assert.Single(f.Db.Integrations);
        Assert.Equal("Slack", integration.Name);
        Assert.Equal(IntegrationStatus.NeedsConfig, integration.Status);
        var action = Assert.Single(f.Db.IntegrationActions);
        Assert.Equal("post_message", action.Name);
        Assert.Equal("POST", action.Method);
    }

    // An integration of that name already exists — reuse it instead of
    // creating a near-duplicate.
    [Fact]
    public async Task Generated_ReusesAnExistingIntegrationByName()
    {
        using var f = new Fixture();
        var existing = SeedIntegration(f.Db, "Slack");
        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element(
                """{"type":"integration_action","code":{"integration_name":"slack","action_name":"post_message"}}"""),
        };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Single(f.Db.Integrations);
        Assert.Equal(existing.IntegrationId, Assert.Single(f.Db.IntegrationActions).IntegrationId);
    }

    // With no integration name in the draft there is nothing to bind to, so
    // the action lands under the catch-all and the import still completes.
    [Fact]
    public async Task Generated_WithoutAnIntegrationNameFallsBackToTheCatchAll()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element("""{"type":"integration_action","code":"not json"}"""),
        };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Equal("imported_actions", Assert.Single(f.Db.Integrations).Name);
        // The action name falls back to the import-side id.
        Assert.Equal("notify_slack", Assert.Single(f.Db.IntegrationActions).Name);
    }

    // Shape 2 in the wild: `code` is a STRING containing the JSON. It has to
    // be parsed, not treated as a python body.
    [Fact]
    public async Task Generated_ParsesHintsFromAStringEncodedCodeField()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element(
                "{\"type\":\"integration_action\",\"code\":"
                + JsonSerializer.Serialize("""{"integration_name":"Slack","action_name":"post_message"}""")
                + "}"),
        };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Equal("Slack", Assert.Single(f.Db.Integrations).Name);
        Assert.Equal("post_message", Assert.Single(f.Db.IntegrationActions).Name);
    }

    // Shape 3: the metadata sits at the top level of the generated snippet.
    [Fact]
    public async Task Generated_ParsesHintsFromTheTopLevelPayload()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element(
                """{"type":"integration_action","integration_name":"Slack","action_name":"post_message"}"""),
        };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Equal("Slack", Assert.Single(f.Db.Integrations).Name);
        Assert.Equal("post_message", Assert.Single(f.Db.IntegrationActions).Name);
    }

    // Mapping the same import-side id twice reuses the action rather than
    // stacking duplicates on every re-import.
    [Fact]
    public async Task Map_ReusesAnActionThatAlreadyMatchesTheImportId()
    {
        using var f = new Fixture();
        var integration = SeedIntegration(f.Db, "Slack");
        var existing = SeedAction(f.Db, integration.IntegrationId, "notify_slack");

        var draft = f.ReadyDraft(Report(
            Workflow("{\"id\":\"a\",\"snippet_id\":\"notify_slack\",\"x\":0,\"y\":0}"),
            new[] { new MissingSnippet { IdInImport = "notify_slack", InferredType = "integration_action" } }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency
        {
            Action = "map", TargetId = integration.IntegrationId,
        };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Single(f.Db.IntegrationActions);
        Assert.Equal(existing.IntegrationActionId.ToString(),
            SavedNode(f, "a").GetProperty("config_overrides").GetProperty("action_id").GetString());
    }
}
