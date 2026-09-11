using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The commit half of the import wizard: the user's per-dependency resolutions
// (stub / generated / map / skip) are applied, the proposed workflow is
// rewritten with the resulting ids, and only then does anything reach the DB.
//
// The invariants worth guarding are the ones a user would notice: an
// unresolved dependency must be rejected rather than silently stubbed, a
// `skip` must take its nodes AND their edges out of the graph, integration_action
// references must never become Snippet rows, and a validation failure after
// staging must not leave orphan snippets behind.
public class WorkflowImportCommitTests
{

    // ─── Doubles ────────────────────────────────────────────────────────

    private sealed class GatedAppSettings : IAppSettingsService
    {
        public bool Granular { get; set; }
        public Task<Services.Settings.AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(Services.Settings.AppSettings.Default with
            {
                PermissionsGranularGatingEnabled = Granular,
            });
        public Task<Services.Settings.AppSettings> UpdateAsync(
            Services.Settings.AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private sealed class ScriptedReferenceValidator : IWorkflowReferenceValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public Task<WorkflowValidationResult> ValidateAsync(JsonElement nodes, CancellationToken ct)
            => Task.FromResult(Result);
        public Task<WorkflowValidationResult> ValidateWithContextAsync(
            JsonElement nodes, string? name, string? description, CancellationToken ct)
            => Task.FromResult(Result);
    }

    private sealed class ScriptedSchemaValidator : IWorkflowSchemaValidator
    {
        public WorkflowValidationResult Result { get; set; } = WorkflowValidationResult.Ok();
        public string CurrentSchemaVersion => "v1";
        public string RawJson => "{}";
        public WorkflowValidationResult Validate(JsonElement nodes, JsonElement edges) => Result;
    }

    // ─── Fixture ────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ImportDraftCache Cache { get; } = new();
        public FakeUser Caller { get; } = new();
        public GatedAppSettings Settings { get; } = new();
        public ScriptedSchemaValidator Schema { get; } = new();
        public ScriptedReferenceValidator References { get; } = new();
        public IResourcePermissionService Permissions { get; set; } = new AllowAllResourcePermissions();

        public WorkflowImportController Build()
        {
            var controller = new WorkflowImportController(
                Cache,
                new EmptyScopeFactory(),
                Caller,
                new FakeAudit(),
                Db,
                Schema,
                References,
                new SnippetStubBuilder(),
                Permissions,
                Settings,
                new UnusedBundleImporter(),
                NullLogger<WorkflowImportController>.Instance);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            };
            return controller;
        }

        // Creates a Ready draft carrying `report` — the state Commit requires.
        public ImportDraft ReadyDraft(AnalysisReport report)
        {
            var draft = Cache.Create(Caller.UserId, "", Encoding.UTF8.GetBytes("{}"));
            draft.MarkAnalyzing();
            draft.MarkReady(report);
            return draft;
        }

        public void Dispose() => Db.Dispose();
    }

    // Analyze's fire-and-forget path is the only consumer of the scope
    // factory; Commit never touches it.
    private sealed class EmptyScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        public IServiceScope CreateScope() => _provider.CreateScope();
    }

    // Grants every per-resource check. The grant/list/revoke members are
    // never reached on the commit path.
    private sealed class AllowAllResourcePermissions : IResourcePermissionService
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

    // ─── Builders ───────────────────────────────────────────────────────

    private static JsonElement Workflow(string nodes, string edges = "[]", string name = "imported")
        => TestJson.Element(
            "{\"name\":" + JsonSerializer.Serialize(name)
            + ",\"nodes\":" + nodes + ",\"edges\":" + edges + "}");

    private static string Node(string id, string snippetId, string type = "task")
        => "{\"id\":" + JsonSerializer.Serialize(id)
           + ",\"snippet_id\":" + JsonSerializer.Serialize(snippetId)
           + ",\"x\":0,\"y\":0,\"type\":\"" + type + "\"}";

    private static string Edge(string source, string target, string type = "success")
        => "{\"source\":" + JsonSerializer.Serialize(source)
           + ",\"target\":" + JsonSerializer.Serialize(target)
           + ",\"type\":\"" + type + "\"}";

    private static AnalysisReport Report(
        JsonElement proposed,
        IReadOnlyList<MissingSnippet>? snippets = null,
        IReadOnlyList<MissingIntegration>? integrations = null,
        ConflictsReport? conflicts = null)
        => new()
        {
            FormatDetected = "itential",
            Confidence = 0.9,
            ProposedWorkflow = proposed,
            MissingDependencies = new DependenciesReport
            {
                Snippets = snippets ?? Array.Empty<MissingSnippet>(),
                Integrations = integrations ?? Array.Empty<MissingIntegration>(),
            },
            Conflicts = conflicts ?? new ConflictsReport(),
        };

    private static MissingSnippet Missing(string id, string type = "python_snippet")
        => new() { IdInImport = id, InferredType = type };

    // Reads the anonymous 200 payload the endpoint returns.
    private static T Read<T>(IActionResult result, string property)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        var prop = value.GetType().GetProperty(property)
            ?? throw new InvalidOperationException($"no '{property}' on the response");
        return (T)prop.GetValue(value)!;
    }

    private static string ProblemDetail(IActionResult result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        return Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(obj.Value).Detail ?? "";
    }

    // ─── Preconditions ──────────────────────────────────────────────────

    [Fact]
    public async Task Commit_AnUnknownTokenIs404()
    {
        using var f = new Fixture();

        var result = await f.Build().Commit(Guid.NewGuid(), new CommitImportRequest(), default);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task Commit_ADraftStillAnalyzingIs409()
    {
        using var f = new Fixture();
        var draft = f.Cache.Create(f.Caller.UserId, "", Encoding.UTF8.GetBytes("{}"));
        draft.MarkAnalyzing();

        var result = await f.Build().Commit(draft.Token, new CommitImportRequest(), default);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("draft not ready", ProblemDetail(result));
    }

    // ─── keep_existing ──────────────────────────────────────────────────

    // Cancelling the import is a success, not an error — the wizard shows
    // "nothing to do" and the choice is audited.
    [Fact]
    public async Task Commit_KeepExistingCancelsTheImportWithoutWritingAnything()
    {
        using var f = new Fixture();
        var existingId = Guid.NewGuid();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                NameCollision = new NameCollision
                {
                    MatchingWorkflowId = existingId,
                    MatchingWorkflowEnvironment = "prod",
                    MatchingWorkflowVersion = 2,
                },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "keep_existing" }, default);

        Assert.Equal(existingId, Read<Guid?>(result, "workflow_id"));
        Assert.Equal("prod", Read<string?>(result, "environment"));
        Assert.Equal(ImportDraftStatus.Committed, draft.Status);
        Assert.Empty(f.Db.Workflows);
    }

    [Fact]
    public async Task Commit_ConflictResolutionMatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "KEEP_EXISTING" }, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(f.Db.Workflows);
    }

    // ─── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public async Task Commit_PersistsTheWorkflowAndMarksTheDraftCommitted()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow(
            "[" + Node("a", "__start__") + "," + Node("b", "__end__") + "]",
            "[" + Edge("a", "b") + "]")));

        var result = await f.Build().Commit(draft.Token, new CommitImportRequest(), default);

        Assert.Equal("imported", Read<string>(result, "name"));
        Assert.Equal("draft", Read<string>(result, "environment"));
        Assert.Equal(ImportDraftStatus.Committed, draft.Status);

        var saved = Assert.Single(f.Db.Workflows);
        Assert.Equal(1, saved.Version);
        Assert.Equal("v1", saved.SchemaVersion);
        Assert.True(saved.IsActive);
    }

    // The wizard lets the user pick the landing environment; without a
    // choice a fresh import must land in `draft`, never straight in prod.
    [Fact]
    public async Task Commit_HonoursTheTargetEnvironment()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { TargetEnvironment = "staging" }, default);

        Assert.Equal("staging", Read<string>(result, "environment"));
    }

    // ─── Missing-snippet resolutions ────────────────────────────────────

    // Every missing dependency needs an explicit decision — silently
    // stubbing one the user never saw would put an empty snippet in their
    // catalogue.
    [Fact]
    public async Task Commit_RejectsAnUnresolvedMissingSnippet()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));

        var result = await f.Build().Commit(draft.Token, new CommitImportRequest(), default);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("python_thing", ProblemDetail(result));
        Assert.Empty(f.Db.Snippets);
    }

    [Fact]
    public async Task Commit_RejectsAnUnknownSnippetAction()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "teleport" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("unknown snippet action", ProblemDetail(result));
    }

    // `stub` creates a real Snippet row and the node is rewritten to point
    // at its GUID — a leftover string id would fail reference validation.
    [Fact]
    public async Task Commit_StubCreatesASnippetAndRewritesTheNode()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "stub" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        var stub = Assert.Single(f.Db.Snippets);

        var saved = Assert.Single(f.Db.Workflows);
        var node = saved.Nodes.EnumerateArray().Single();
        Assert.Equal(stub.SnippetId.ToString(), node.GetProperty("snippet_id").GetString());
    }

    // `map` binds the node to an existing snippet the user picked; no new
    // row is created.
    [Fact]
    public async Task Commit_MapBindsToTheChosenExistingSnippet()
    {
        using var f = new Fixture();
        var target = Guid.NewGuid();
        f.Db.Snippets.Add(new SnippetModel
        {
            SnippetId = target, Name = "existing", Type = "python_snippet", IsActive = true,
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "map", TargetId = target };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(f.Db.Snippets);
        var node = Assert.Single(f.Db.Workflows).Nodes.EnumerateArray().Single();
        Assert.Equal(target.ToString(), node.GetProperty("snippet_id").GetString());
    }

    [Fact]
    public async Task Commit_MapWithoutATargetIsRejected()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "map" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("requires target_id", ProblemDetail(result));
    }

    [Fact]
    public async Task Commit_GeneratedWithoutAPayloadIsRejected()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "generated" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("requires generated_snippet", ProblemDetail(result));
    }

    // The AI-drafted body lands as a real snippet with the generated name
    // and code — not as a stub with the placeholder id.
    [Fact]
    public async Task Commit_GeneratedPersistsTheDraftedBody()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency
        {
            Action = "generated",
            GeneratedSnippet = TestJson.Element(
                """{"name":"transform_devices","type":"python_snippet","code":"return {}"}"""),
        };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        var saved = Assert.Single(f.Db.Snippets);
        Assert.Equal("transform_devices", saved.Name);
        Assert.Equal("python_snippet", saved.Type);
    }

    // ─── skip ───────────────────────────────────────────────────────────

    // A skipped snippet takes its node out of the graph. Leaving the node
    // behind would fail reference validation on an id that resolves to
    // nothing.
    [Fact]
    public async Task Commit_SkipDropsTheNodeAndItsIncidentEdges()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow(
                "[" + Node("a", "__start__") + "," + Node("b", "python_thing") + "," + Node("c", "__end__") + "]",
                "[" + Edge("a", "b") + "," + Edge("b", "c") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "skip" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(f.Db.Snippets);

        var saved = Assert.Single(f.Db.Workflows);
        var nodeIds = saved.Nodes.EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "a", "c" }, nodeIds);
        Assert.Empty(saved.Edges.EnumerateArray());
    }

    // Dropping nodes leaves the graph disconnected, so the user is warned
    // rather than silently handed a broken workflow.
    [Fact]
    public async Task Commit_SkipWarnsAboutWhatItRemoved()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow(
                "[" + Node("a", "__start__") + "," + Node("b", "python_thing") + "]",
                "[" + Edge("a", "b") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "skip" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.NotEmpty(Read<string[]>(result, "warnings"));
    }

    // ─── integration_action stubs ───────────────────────────────────────

    // An integration_action reference must NOT become a Snippet row — a
    // `Type = "integration_action"` snippet fails the reference validator,
    // which demands integration_id + action_id on the node.
    [Fact]
    public async Task Commit_AnIntegrationActionStubBecomesAnIntegrationNotASnippet()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "]"),
            snippets: new[] { Missing("notify_slack", "integration_action") }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "stub" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(f.Db.Snippets);

        var integration = Assert.Single(f.Db.Integrations);
        Assert.Equal("imported_actions", integration.Name);
        Assert.Equal(IntegrationStatus.NeedsConfig, integration.Status);

        var action = Assert.Single(f.Db.IntegrationActions);
        Assert.Equal("notify_slack", action.Name);
        Assert.Equal(integration.IntegrationId, action.IntegrationId);
        // The palette query filters on Enabled AND IsActive — without both
        // the node's action picker renders empty.
        Assert.True(action.Enabled);
        Assert.True(action.IsActive);
    }

    // The node is rewritten to the virtual sentinel carrying both GUIDs.
    [Fact]
    public async Task Commit_AnIntegrationActionNodeCarriesBothIdsInConfigOverrides()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "]"),
            snippets: new[] { Missing("notify_slack", "integration_action") }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "stub" };

        await f.Build().Commit(draft.Token, dto, default);

        var node = Assert.Single(f.Db.Workflows).Nodes.EnumerateArray().Single();
        Assert.Equal("integration_action", node.GetProperty("snippet_id").GetString());
        var overrides = node.GetProperty("config_overrides");
        var action = Assert.Single(f.Db.IntegrationActions);
        Assert.Equal(action.IntegrationId.ToString(), overrides.GetProperty("integration_id").GetString());
        Assert.Equal(action.IntegrationActionId.ToString(), overrides.GetProperty("action_id").GetString());
    }

    // Two integration_action stubs in one import share a single catch-all
    // integration rather than creating one each.
    [Fact]
    public async Task Commit_SeveralActionStubsShareOneCatchAllIntegration()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "," + Node("b", "notify_email") + "]"),
            snippets: new[]
            {
                Missing("notify_slack", "integration_action"),
                Missing("notify_email", "integration_action"),
            }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "stub" };
        dto.Snippets["notify_email"] = new ResolvedDependency { Action = "stub" };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Single(f.Db.Integrations);
        Assert.Equal(2, f.Db.IntegrationActions.Count());
    }

    // An existing catch-all is reused across imports; a second import must
    // not add a duplicate `imported_actions` row.
    [Fact]
    public async Task Commit_AnExistingCatchAllIsReused()
    {
        using var f = new Fixture();
        var existing = Guid.NewGuid();
        f.Db.Integrations.Add(new IntegrationModel
        {
            IntegrationId = existing,
            Name = "imported_actions",
            Type = "generic_rest",
            BaseURL = "",
            AuthConfig = TestJson.Element("{}"),
            Headers = TestJson.Element("{}"),
            HealthCheck = TestJson.Element("{}"),
            Status = IntegrationStatus.NeedsConfig,
            Enabled = true,
            IsActive = true,
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "]"),
            snippets: new[] { Missing("notify_slack", "integration_action") }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "stub" };

        await f.Build().Commit(draft.Token, dto, default);

        Assert.Single(f.Db.Integrations);
        Assert.Equal(existing, Assert.Single(f.Db.IntegrationActions).IntegrationId);
    }

    // Mapping an integration_action to an integration the user picked
    // materialises the action under THAT integration, not the catch-all.
    [Fact]
    public async Task Commit_MappingAnActionStubUsesTheChosenIntegration()
    {
        using var f = new Fixture();
        var chosen = Guid.NewGuid();
        f.Db.Integrations.Add(new IntegrationModel
        {
            IntegrationId = chosen,
            Name = "Slack",
            Type = "generic_rest",
            BaseURL = "https://slack.test",
            AuthConfig = TestJson.Element("{}"),
            Headers = TestJson.Element("{}"),
            HealthCheck = TestJson.Element("{}"),
            Status = IntegrationStatus.Healthy,
            Enabled = true,
            IsActive = true,
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "]"),
            snippets: new[] { Missing("notify_slack", "integration_action") }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "map", TargetId = chosen };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(f.Db.Integrations);
        Assert.Equal(chosen, Assert.Single(f.Db.IntegrationActions).IntegrationId);
    }

    // A target id that doesn't resolve must be rejected rather than
    // producing a node pointing at a non-existent integration.
    [Fact]
    public async Task Commit_MappingAnActionStubToAMissingIntegrationIsRejected()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "notify_slack") + "]"),
            snippets: new[] { Missing("notify_slack", "integration_action") }));
        var dto = new CommitImportRequest();
        dto.Snippets["notify_slack"] = new ResolvedDependency { Action = "map", TargetId = Guid.NewGuid() };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("not found", ProblemDetail(result));
    }

    // ─── Missing-integration resolutions ────────────────────────────────

    [Fact]
    public async Task Commit_RejectsAnUnresolvedMissingIntegration()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            integrations: new[] { new MissingIntegration { IdInImport = "jira" } }));

        var result = await f.Build().Commit(draft.Token, new CommitImportRequest(), default);

        Assert.Contains("jira", ProblemDetail(result));
    }

    [Fact]
    public async Task Commit_RejectsAnUnknownIntegrationAction()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            integrations: new[] { new MissingIntegration { IdInImport = "jira" } }));
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "teleport" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("unknown integration action", ProblemDetail(result));
    }

    // A created integration lands in needs_config with the inferred base
    // URL pre-filled, and the user is told to configure it before running.
    [Fact]
    public async Task Commit_CreateNeedsConfigMaterialisesTheIntegrationAndWarns()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            integrations: new[]
            {
                new MissingIntegration
                {
                    IdInImport = "jira",
                    InferredBaseUrl = "https://jira.test",
                    InferredType = "generic_rest",
                },
            }));
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "create_needs_config" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        var created = Assert.Single(f.Db.Integrations);
        Assert.Equal("jira", created.Name);
        Assert.Equal("https://jira.test", created.BaseURL);
        Assert.Equal(IntegrationStatus.NeedsConfig, created.Status);
        Assert.Contains(Read<string[]>(result, "warnings"), w => w.Contains("needs_config"));
    }

    [Fact]
    public async Task Commit_MappingAnIntegrationCreatesNothingAndDoesNotWarn()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            integrations: new[] { new MissingIntegration { IdInImport = "jira" } }));
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "map", TargetId = Guid.NewGuid() };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Empty(f.Db.Integrations);
        Assert.DoesNotContain(Read<string[]>(result, "warnings"), w => w.Contains("needs_config"));
    }

    [Fact]
    public async Task Commit_MappingAnIntegrationWithoutATargetIsRejected()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            integrations: new[] { new MissingIntegration { IdInImport = "jira" } }));
        var dto = new CommitImportRequest();
        dto.Integrations["jira"] = new ResolvedDependency { Action = "map" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.Contains("requires target_id", ProblemDetail(result));
    }

    // ─── replace ────────────────────────────────────────────────────────

    // `replace` soft-deletes the colliding workflow so the import lands
    // without a duplicate name.
    [Fact]
    public async Task Commit_ReplaceSoftDeletesTheCollidingWorkflow()
    {
        using var f = new Fixture();
        var existingId = Guid.NewGuid();
        f.Db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = existingId, Name = "imported",
            Environment = "draft", Version = 1, IsActive = true,
            Nodes = TestJson.Element("[]"), Edges = TestJson.Element("[]"),
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                NameCollision = new NameCollision
                {
                    MatchingWorkflowId = existingId,
                    MatchingWorkflowEnvironment = "draft",
                    MatchingWorkflowVersion = 1,
                },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "replace" }, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(f.Db.Workflows.Single(w => w.WorkflowId == existingId).IsActive);
        Assert.Equal(2, f.Db.Workflows.Count());
    }

    // With granular gating on, replacing needs an editor grant on the
    // target — the global Operator policy is not enough.
    [Fact]
    public async Task Commit_ReplaceWithoutAnEditorGrantIs403()
    {
        using var f = new Fixture();
        f.Settings.Granular = true;
        f.Permissions = new DenyAllPermissions();
        var existingId = Guid.NewGuid();
        f.Db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = existingId, Name = "imported",
            Environment = "draft", Version = 1, IsActive = true,
            Nodes = TestJson.Element("[]"), Edges = TestJson.Element("[]"),
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                NameCollision = new NameCollision { MatchingWorkflowId = existingId },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "replace" }, default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(f.Db.Workflows.Single(w => w.WorkflowId == existingId).IsActive);
    }

    // Granular gating is opt-in: with it off, the same denying permission
    // service must not block the replace.
    [Fact]
    public async Task Commit_ReplaceIsUngatedWhenGranularGatingIsNotEnabled()
    {
        using var f = new Fixture();
        f.Settings.Granular = false;
        f.Permissions = new DenyAllPermissions();
        var existingId = Guid.NewGuid();
        f.Db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = existingId, Name = "imported",
            Environment = "draft", Version = 1, IsActive = true,
            Nodes = TestJson.Element("[]"), Edges = TestJson.Element("[]"),
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                NameCollision = new NameCollision { MatchingWorkflowId = existingId },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "replace" }, default);

        Assert.IsType<OkObjectResult>(result);
    }

    // ─── rename / fresh_copy ────────────────────────────────────────────

    [Fact]
    public async Task Commit_RenameUsesTheNameTheUserTyped()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var result = await f.Build().Commit(
            draft.Token,
            new CommitImportRequest { ConflictResolution = "rename", NewName = "imported v2" },
            default);

        Assert.Equal("imported v2", Read<string>(result, "name"));
    }

    // fresh_copy against a name collision must not reuse the colliding
    // name verbatim — the suffix is what keeps the two apart in the list.
    [Fact]
    public async Task Commit_FreshCopyDisambiguatesAgainstACollision()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                NameCollision = new NameCollision { MatchingWorkflowId = Guid.NewGuid() },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "fresh_copy" }, default);

        Assert.NotEqual("imported", Read<string>(result, "name"));
        Assert.StartsWith("imported", Read<string>(result, "name"));
    }

    // ─── update_existing ────────────────────────────────────────────────

    // Updating in place keeps the WorkflowId so grants, audit history and
    // outstanding runs stay pointed at the right row, bumps the version,
    // and snapshots the previous state for rollback.
    [Fact]
    public async Task Commit_UpdateExistingMutatesInPlaceAndSnapshotsThePreviousVersion()
    {
        using var f = new Fixture();
        var dupId = Guid.NewGuid();
        f.Db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = dupId, Name = "old name",
            Environment = "prod", Version = 4, IsActive = true,
            CreatedBy = "someone-else",
            Nodes = TestJson.Element("[]"), Edges = TestJson.Element("[]"),
            Metadata = TestJson.Element("""{"is_subflow":true}"""),
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "__start__") + "]"),
            conflicts: new ConflictsReport
            {
                StructuralDuplicate = new StructuralDuplicate
                {
                    MatchingWorkflowId = dupId,
                    MatchingWorkflowName = "old name",
                    MatchingWorkflowEnvironment = "prod",
                },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { DuplicateAction = "update_existing" }, default);

        Assert.Equal(dupId, Read<Guid>(result, "workflow_id"));
        var saved = Assert.Single(f.Db.Workflows);
        Assert.Equal(5, saved.Version);
        Assert.Equal("imported", saved.Name);
        // Environment and authorship are deliberately preserved.
        Assert.Equal("prod", saved.Environment);
        Assert.Equal("someone-else", saved.CreatedBy);
        // Metadata is merged, so flags like is_subflow survive the import.
        Assert.True(saved.Metadata.GetProperty("is_subflow").GetBoolean());

        var snapshot = Assert.Single(f.Db.WorkflowVersions);
        Assert.Equal(4, snapshot.Version);
        Assert.Equal(dupId, snapshot.WorkflowId);
    }

    // update_existing without a structural duplicate in the report has
    // nothing to update — it falls through to a normal insert.
    [Fact]
    public async Task Commit_UpdateExistingWithoutADuplicateInsertsANewWorkflow()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { DuplicateAction = "update_existing" }, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, Assert.Single(f.Db.Workflows).Version);
        Assert.Empty(f.Db.WorkflowVersions);
    }

    [Fact]
    public async Task Commit_UpdateExistingWithoutAnEditorGrantIs403()
    {
        using var f = new Fixture();
        f.Settings.Granular = true;
        f.Permissions = new DenyAllPermissions();
        var dupId = Guid.NewGuid();
        f.Db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = dupId, Name = "old name",
            Environment = "prod", Version = 4, IsActive = true,
            Nodes = TestJson.Element("[]"), Edges = TestJson.Element("[]"),
        });
        f.Db.SaveChanges();

        var draft = f.ReadyDraft(Report(
            Workflow("[]"),
            conflicts: new ConflictsReport
            {
                StructuralDuplicate = new StructuralDuplicate { MatchingWorkflowId = dupId },
            }));

        var result = await f.Build().Commit(
            draft.Token, new CommitImportRequest { DuplicateAction = "update_existing" }, default);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(4, f.Db.Workflows.Single().Version);
    }

    // ─── Validation gates ───────────────────────────────────────────────

    // A schema failure happens BEFORE anything is saved, so the staged
    // stub must not reach the DB.
    [Fact]
    public async Task Commit_ASchemaFailureIs400AndPersistsNothing()
    {
        using var f = new Fixture();
        f.Schema.Result = WorkflowValidationResult.Invalid(new[] { "nodes[0].x: required" });
        var draft = f.ReadyDraft(Report(
            Workflow("[" + Node("a", "python_thing") + "]"),
            snippets: new[] { Missing("python_thing") }));
        var dto = new CommitImportRequest();
        dto.Snippets["python_thing"] = new ResolvedDependency { Action = "stub" };

        var result = await f.Build().Commit(draft.Token, dto, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(f.Db.Workflows);
        Assert.Empty(f.Db.Snippets);
        Assert.NotEqual(ImportDraftStatus.Committed, draft.Status);
    }

    // A reference failure surfaces the validator's own errors so the user
    // can correct the resolution and retry.
    [Fact]
    public async Task Commit_AReferenceFailureIs400AndSurfacesTheErrors()
    {
        using var f = new Fixture();
        f.References.Result = WorkflowValidationResult.Invalid(new[] { "snippet 'abc' not found" });
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var result = await f.Build().Commit(draft.Token, new CommitImportRequest(), default);

        var body = Assert.IsType<BadRequestObjectResult>(result).Value!;
        var details = (IReadOnlyList<string>)body.GetType().GetProperty("details")!.GetValue(body)!;
        Assert.Contains("snippet 'abc' not found", details);
        Assert.Empty(f.Db.Workflows);
    }

    // ─── Get / Delete ───────────────────────────────────────────────────

    [Fact]
    public void Get_ReturnsTheDraftState()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));

        var body = Assert.IsType<OkObjectResult>(f.Build().Get(draft.Token)).Value!;

        Assert.Equal(draft.Token, body.GetType().GetProperty("import_token")!.GetValue(body));
        Assert.Equal("ready", body.GetType().GetProperty("status")!.GetValue(body));
        Assert.NotNull(body.GetType().GetProperty("report")!.GetValue(body));
    }

    [Fact]
    public void Get_AnUnknownTokenIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, Assert.IsType<ObjectResult>(f.Build().Get(Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public void Delete_RemovesTheDraftAndIsIdempotent()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));
        var controller = f.Build();

        Assert.IsType<NoContentResult>(controller.Delete(draft.Token));
        Assert.Equal(404, Assert.IsType<ObjectResult>(controller.Get(draft.Token)).StatusCode);
        // Deleting again is still a 204 — the wizard's cleanup runs on
        // unmount and must not fail if the user already cancelled.
        Assert.IsType<NoContentResult>(controller.Delete(draft.Token));
    }

    // Deleting cancels the draft's token so an in-flight pipeline stops
    // burning LLM calls.
    [Fact]
    public void Delete_CancelsTheDraftsToken()
    {
        using var f = new Fixture();
        var draft = f.ReadyDraft(Report(Workflow("[]")));
        var token = draft.CancellationToken;

        f.Build().Delete(draft.Token);

        Assert.True(token.IsCancellationRequested);
    }

}

// The bundle path is not what these fixtures exercise: they build drafts whose
// report has no `bundle`, so Commit never reaches the importer. Present because
// the controller now takes one, and it throws rather than returning a plausible
// empty result — a test that silently took the bundle branch would otherwise pass
// while asserting nothing.
internal sealed class UnusedBundleImporter : flow_weaver_backend.Services.Workflow.IWorkflowBundleImporter
{
    public Task<flow_weaver_backend.Dtos.WorkflowResponse> ImportAsync(
        flow_weaver_backend.Services.Workflow.WorkflowBundle bundle, CancellationToken ct)
        => throw new InvalidOperationException(
            "the bundle importer must not be reached by a non-bundle draft");
}
