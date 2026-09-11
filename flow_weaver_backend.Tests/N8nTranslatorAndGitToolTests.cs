using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Tools.Handlers.Git;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Import.Translators;
using Microsoft.AspNetCore.Mvc;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// The n8n importer, the agent's git-webhook listing, and the run repository's
// query surface.
//
// The translator is the one with consequences: an n8n node type it doesn't
// recognise must be surfaced as a missing snippet the wizard can resolve, not
// silently dropped — a dropped node is a step the imported workflow never
// performs.
public class N8nTranslatorAndGitToolTests
{

    // ─── N8nTranslator ──────────────────────────────────────────────────

    private static async Task<(JsonElement Workflow, IReadOnlyList<string> Notes)> Translate(string document)
    {
        var result = await new N8nTranslator().TranslateAsync(TestJson.Element(document), default);
        return (result.V1Workflow, result.Notes);
    }

    private static List<string> NodeIds(JsonElement workflow)
        => workflow.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()!)
            .ToList();

    private static string SnippetIdOf(JsonElement workflow, string nodeId)
        => workflow.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == nodeId)
            .GetProperty("snippet_id").GetString()!;

    private static string N8nDoc(string nodes, string connections = "{}", string name = "wf")
        => "{\"name\":" + JsonSerializer.Serialize(name)
           + ",\"nodes\":" + nodes + ",\"connections\":" + connections + "}";

    private static string N8nNode(string nodeName, string type)
        => "{\"name\":" + JsonSerializer.Serialize(nodeName)
           + ",\"type\":" + JsonSerializer.Serialize(type) + ",\"parameters\":{}}";

    [Fact]
    public void N8n_TheTranslatorRegistersUnderItsFormat()
    {
        Assert.Equal("n8n", new N8nTranslator().FormatName);
    }

    // Sentinels are synthesised so the imported graph has the start/end the
    // engine expects, whatever n8n's own shape was.
    [Fact]
    public async Task N8n_SentinelsAreAlwaysAdded()
    {
        var (workflow, _) = await Translate(N8nDoc("[]"));

        var ids = NodeIds(workflow);
        Assert.Contains("__start__", ids);
        Assert.Contains("__end__", ids);
    }

    [Fact]
    public async Task N8n_AnUnnamedDocumentGetsAFallbackName()
    {
        var (workflow, _) = await Translate("""{"nodes":[]}""");

        Assert.Equal("n8n import", workflow.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("n8n-nodes-base.httpRequest", "rest_call")]
    [InlineData("n8n-nodes-base.code", "python_snippet")]
    [InlineData("n8n-nodes-base.function", "python_snippet")]
    [InlineData("n8n-nodes-base.executeCommand", "ssh")]
    [InlineData("n8n-nodes-base.emailSend", "integration_action")]
    [InlineData("n8n-nodes-base.slack", "integration_action")]
    [InlineData("n8n-nodes-base.webhook", "rest_call")]
    public async Task N8n_EachKnownTypeMapsToItsSnippetType(string n8nType, string expected)
    {
        var (workflow, _) = await Translate(N8nDoc("[" + N8nNode("step", n8nType) + "]"));

        Assert.Equal(expected, SnippetIdOf(workflow, "step"));
    }

    // An unmapped type keeps its own name as the snippet id, which the
    // dependency resolver then reports as missing — the wizard offers to map
    // or generate it. Dropping the node would silently lose a step.
    [Fact]
    public async Task N8n_AnUnmappedTypeIsSurfacedNotDropped()
    {
        var (workflow, notes) = await Translate(
            N8nDoc("[" + N8nNode("weird", "n8n-nodes-base.notion") + "]"));

        Assert.Contains("weird", NodeIds(workflow));
        Assert.Equal("n8n-nodes-base.notion", SnippetIdOf(workflow, "weird"));
        Assert.Contains(notes, n => n.Contains("Unmapped n8n type"));
    }

    // Node names become ids, so they have to be sanitised into something the
    // v1 schema and the template grammar accept.
    [Fact]
    public async Task N8n_NodeNamesAreSanitisedIntoUsableIds()
    {
        var (workflow, _) = await Translate(
            N8nDoc("[" + N8nNode("HTTP Request 1!", "n8n-nodes-base.httpRequest") + "]"));

        var id = NodeIds(workflow).Single(i => i is not ("__start__" or "__end__"));
        Assert.DoesNotContain(" ", id);
        Assert.DoesNotContain("!", id);
    }

    // n8n addresses nodes by NAME in `connections`, so the translator has to
    // map those names onto the sanitised ids or every edge dangles.
    [Fact]
    public async Task N8n_ConnectionsBecomeEdgesBetweenTheSanitisedIds()
    {
        var (workflow, _) = await Translate(N8nDoc(
            "[" + N8nNode("Fetch Data", "n8n-nodes-base.httpRequest") + ","
                + N8nNode("Run Code", "n8n-nodes-base.code") + "]",
            """
            {"Fetch Data":{"main":[[{"node":"Run Code","type":"main","index":0}]]}}
            """));

        var edges = workflow.GetProperty("edges").EnumerateArray()
            .Select(e => (e.GetProperty("source").GetString()!, e.GetProperty("target").GetString()!))
            .ToList();
        var ids = NodeIds(workflow);
        Assert.Contains(edges, e => ids.Contains(e.Item1) && ids.Contains(e.Item2)
                                    && e.Item1 != "__start__" && e.Item2 != "__end__");
    }

    // KNOWN LIMITATION: unlike ItentialTranslator — which drops a transition
    // whose target isn't in the document — this translator emits the edge
    // with the raw n8n name. The dangling edge is caught downstream:
    // DagParser.Parse rejects it at enqueue time, so a broken import fails
    // loudly rather than running a truncated graph. Pinned as-is; making the
    // two translators agree is a behaviour change, not a bug fix.
    [Fact]
    public async Task N8n_AConnectionToAnUnknownNodeIsEmittedAndCaughtDownstream()
    {
        var (workflow, _) = await Translate(N8nDoc(
            "[" + N8nNode("Fetch", "n8n-nodes-base.httpRequest") + "]",
            """{"Fetch":{"main":[[{"node":"Ghost","type":"main","index":0}]]}}"""));

        Assert.Contains(workflow.GetProperty("edges").EnumerateArray(),
            e => e.GetProperty("target").GetString() == "Ghost");
        Assert.DoesNotContain("Ghost", NodeIds(workflow));
    }

    [Theory]
    [InlineData("""{"name":"wf"}""")]
    [InlineData("""{"name":"wf","nodes":{}}""")]
    [InlineData("""{"name":"wf","nodes":"nope"}""")]
    public async Task N8n_ADocumentWithoutNodesStillYieldsASkeleton(string document)
    {
        var (workflow, _) = await Translate(document);

        Assert.Equal(new[] { "__start__", "__end__" }, NodeIds(workflow));
    }

    [Fact]
    public async Task N8n_TheOutputCarriesTheV1SchemaAndSourceFormat()
    {
        var (workflow, _) = await Translate(N8nDoc("[]"));

        Assert.Equal("v1", workflow.GetProperty("schema_version").GetString());
        Assert.Equal("n8n", workflow.GetProperty("metadata").GetProperty("source_format").GetString());
    }

    // Every emitted node carries the fields the v1 schema requires, so the
    // import validates immediately after translation.
    [Fact]
    public async Task N8n_EveryNodeCarriesTheRequiredV1Fields()
    {
        var (workflow, _) = await Translate(
            N8nDoc("[" + N8nNode("step", "n8n-nodes-base.httpRequest") + "]"));

        foreach (var node in workflow.GetProperty("nodes").EnumerateArray())
        {
            Assert.True(node.TryGetProperty("id", out _));
            Assert.True(node.TryGetProperty("snippet_id", out _));
            Assert.True(node.TryGetProperty("x", out _));
            Assert.True(node.TryGetProperty("y", out _));
            Assert.True(node.TryGetProperty("config_overrides", out _));
        }
    }

    // ─── git_list_webhooks ──────────────────────────────────────────────

    private sealed class StubGitWebhookService : IGitWebhookService
    {
        public List<GitWebhookResponse> Webhooks { get; } = new();
        public ActionResult<ListResponse<GitWebhookResponse>>? Override { get; set; }

        public Task<ActionResult<ListResponse<GitWebhookResponse>>> ListAsync(Guid repoId, CancellationToken ct)
            => Task.FromResult(Override ?? new ListResponse<GitWebhookResponse>
            {
                Data = Webhooks,
                Total = Webhooks.Count,
                Limit = Webhooks.Count,
                Offset = 0,
            });

        // Everything past the list path is unreachable from the tool under test.
        public Task<ActionResult<GitWebhookResponse>> GetAsync(
            Guid repoId, Guid webhookId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> CreateAsync(
            Guid repoId, CreateGitWebhook dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> UpdateAsync(
            Guid repoId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitWebhookResponse>> DeleteAsync(
            Guid repoId, Guid webhookId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<ListResponse<GitWebhookDeliveryResponse>>> ListDeliveriesAsync(
            Guid repoId, Guid webhookId, int limit, CancellationToken ct) => throw new NotSupportedException();
    }

    private static GitWebhookResponse Webhook(string name = "push-hook", bool hasSecret = true)
        => new()
        {
            GitWebhookId = Guid.NewGuid(),
            Name = name,
            Provider = "github",
            IngestionUrl = "https://app.test/api/git/webhooks/abc",
            HasSecret = hasSecret,
            OnPushWorkflowId = Guid.NewGuid(),
            OnPushBranches = new List<string> { "main" },
            AutoPull = true,
            Enabled = true,
        };

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"repository_id":"not-a-guid"}""")]
    [InlineData("""{"repository_id":123}""")]
    public async Task GitWebhooks_ABadRepositoryIdIsAnErrorPayload(string args)
    {
        var handler = new GitListWebhooksHandler(new StubGitWebhookService());

        var result = await handler.ExecuteAsync(TestJson.Element(args), default);

        Assert.Equal("repository_id is required", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GitWebhooks_ListsTheReposHooks()
    {
        var service = new StubGitWebhookService();
        service.Webhooks.Add(Webhook("push-hook"));
        var handler = new GitListWebhooksHandler(service);

        var result = await handler.ExecuteAsync(
            TestJson.Element("{\"repository_id\":\"" + Guid.NewGuid() + "\"}"), default);

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        var hook = result.GetProperty("webhooks").EnumerateArray().Single();
        Assert.Equal("push-hook", hook.GetProperty("name").GetString());
        Assert.Equal("github", hook.GetProperty("provider").GetString());
        Assert.True(hook.GetProperty("auto_pull").GetBoolean());
        Assert.Equal("main",
            hook.GetProperty("on_push_branches").EnumerateArray().Single().GetString());
    }

    // The shared secret is reported as a boolean only — the agent (and its
    // transcript) must never see the value.
    [Fact]
    public async Task GitWebhooks_TheSecretIsReportedAsAFlagNotAValue()
    {
        var service = new StubGitWebhookService();
        service.Webhooks.Add(Webhook(hasSecret: true));
        var handler = new GitListWebhooksHandler(service);

        var result = await handler.ExecuteAsync(
            TestJson.Element("{\"repository_id\":\"" + Guid.NewGuid() + "\"}"), default);

        var hook = result.GetProperty("webhooks").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.True, hook.GetProperty("has_secret").ValueKind);
        Assert.False(hook.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task GitWebhooks_AnEmptyListIsReportedAsZero()
    {
        var handler = new GitListWebhooksHandler(new StubGitWebhookService());

        var result = await handler.ExecuteAsync(
            TestJson.Element("{\"repository_id\":\"" + Guid.NewGuid() + "\"}"), default);

        Assert.Equal(0, result.GetProperty("total").GetInt32());
        Assert.Empty(result.GetProperty("webhooks").EnumerateArray());
    }

    // A service refusal (unknown repo, no access) comes back as a payload the
    // model can read rather than an exception.
    [Fact]
    public async Task GitWebhooks_AServiceRefusalBecomesAnErrorPayload()
    {
        var service = new StubGitWebhookService
        {
            Override = new NotFoundObjectResult(new { error = "repository not found" }),
        };
        var handler = new GitListWebhooksHandler(service);

        var result = await handler.ExecuteAsync(
            TestJson.Element("{\"repository_id\":\"" + Guid.NewGuid() + "\"}"), default);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(result.TryGetProperty("webhooks", out _));
    }

    // ─── WorkflowRunRepository ──────────────────────────────────────────

    private static Guid SeedRun(
        AppDbContext db, Guid workflowId, string status = RunStatus.Completed,
        bool active = true, DateTime? createdAt = null)
    {
        var id = Guid.NewGuid();
        db.WorkflowRuns.Add(new WorkflowRunModel
        {
            WorkflowRunId = id,
            WorkflowId = workflowId,
            Status = status,
            InputPayload = TestJson.Element("{}"),
            TargetDevices = new List<Guid>(),
            TargetPools = new List<Guid>(),
            IsActive = active,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    // A soft-deleted run is still reachable with activeOnly:false — the agent
    // is routinely asked about archived runs.
    [Fact]
    public async Task Runs_SoftDeletedRunsAreReachableOnlyWhenAsked()
    {
        using var db = TestDb.NewContext();
        var repo = new WorkflowRunRepository(db);
        var id = SeedRun(db, Guid.NewGuid(), active: false);

        Assert.Null(await repo.GetByIdAsync(id));
        Assert.NotNull(await repo.GetByIdAsync(id, activeOnly: false));
    }

}
