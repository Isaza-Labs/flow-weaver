using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.RestExecutor;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Detectors;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Import.Translators;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// Three of the agent's tools. They share one contract that matters more than
// their individual logic: a tool call must always come back as a JSON object
// the model can read — a bad argument becomes `{ "error": ... }`, never an
// exception that aborts the whole chat turn.
public class AiToolHandlerBatchTests
{

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static JsonElement Args(string json) => TestJson.Element(json);

    // ─── execute_operation ──────────────────────────────────────────────

    private sealed class ScriptedExecutor : IRestOperationExecutor
    {
                // Body must be a real JsonElement: the production executor always
        // fills it (`null` literal on the failure path), and serialising an
        // uninitialised one throws.
        public RestExecutionResult Result { get; set; } =
            new() { StatusCode = 200, Body = TestJson.Element("null") };
        public Exception? Throw { get; set; }
        public List<(string OperationId, string Path, string Query, string Body)> Calls { get; } = new();

        public Task<RestExecutionResult> ExecuteAsync(
            string operationId, JsonElement pathParams, JsonElement queryParams,
            JsonElement body, CancellationToken ct)
        {
            Calls.Add((operationId, pathParams.GetRawText(), queryParams.GetRawText(), body.GetRawText()));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private static ExecuteOperationHandler Execute(ScriptedExecutor executor)
        => new(executor, NullLogger<ExecuteOperationHandler>.Instance);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"operation_id":123}""")]
    [InlineData("""{"operation_id":null}""")]
    public async Task ExecuteOperation_AMissingOperationIdIsAnErrorPayloadNotAnException(string args)
    {
        var executor = new ScriptedExecutor();

        var result = await Execute(executor).ExecuteAsync(Args(args), default);

        Assert.Equal("operation_id is required", Error(result));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task ExecuteOperation_ForwardsTheResponseVerbatim()
    {
        var executor = new ScriptedExecutor
        {
            Result = new RestExecutionResult
            {
                StatusCode = 201,
                Body = TestJson.Element("""{"id":7}"""),
                Headers = new Dictionary<string, string> { ["X-Rate"] = "9" },
            },
        };

        var result = await Execute(executor).ExecuteAsync(
            Args("""{"operation_id":"netbox:createDevice"}"""), default);

        Assert.Equal(201, result.GetProperty("status_code").GetInt32());
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(7, result.GetProperty("body").GetProperty("id").GetInt32());
        Assert.Equal("9", result.GetProperty("headers").GetProperty("X-Rate").GetString());
    }

    // A 4xx is a real answer for the model to reason about, not a tool
    // failure — it comes back with success=false and the error attached.
    [Fact]
    public async Task ExecuteOperation_AFailedCallIsReportedNotThrown()
    {
        var executor = new ScriptedExecutor
        {
            Result = new RestExecutionResult { StatusCode = 404, Body = TestJson.Element("null"), Error = "not found" },
        };

        var result = await Execute(executor).ExecuteAsync(
            Args("""{"operation_id":"netbox:getDevice"}"""), default);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("not found", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ExecuteOperation_PathAndQueryParamsAreForwarded()
    {
        var executor = new ScriptedExecutor();

        await Execute(executor).ExecuteAsync(Args(
            """{"operation_id":"netbox:getDevice","path_params":{"id":"7"},"query_params":{"brief":"true"}}"""),
            default);

        var call = Assert.Single(executor.Calls);
        Assert.Contains("\"id\":\"7\"", call.Path);
        Assert.Contains("\"brief\":\"true\"", call.Query);
    }

    // Non-object params would break the executor's template substitution, so
    // they degrade to an empty object rather than being passed through.
    [Theory]
    [InlineData("""{"operation_id":"x","path_params":"not-an-object"}""")]
    [InlineData("""{"operation_id":"x","path_params":[1,2]}""")]
    [InlineData("""{"operation_id":"x"}""")]
    public async Task ExecuteOperation_NonObjectParamsBecomeAnEmptyObject(string args)
    {
        var executor = new ScriptedExecutor();

        await Execute(executor).ExecuteAsync(Args(args), default);

        Assert.Equal("{}", Assert.Single(executor.Calls).Path);
    }

    [Fact]
    public async Task ExecuteOperation_AnAbsentBodyBecomesJsonNull()
    {
        var executor = new ScriptedExecutor();

        await Execute(executor).ExecuteAsync(Args("""{"operation_id":"x"}"""), default);

        Assert.Equal("null", Assert.Single(executor.Calls).Body);
    }

    // ─── discover_operations ────────────────────────────────────────────

    private sealed class StubIndex : IApiSpecIndex
    {
        public List<ApiOperation> Operations { get; } = new();
        public List<(string Keyword, string? Api, string? Method)> Searches { get; } = new();

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => Operations;
        public IReadOnlyList<ApiOperation> Search(
            string keyword, string? api = null, string? method = null)
        {
            Searches.Add((keyword, api, method));
            return Operations;
        }
        public ApiOperation? GetByOperationId(string operationId)
            => Operations.FirstOrDefault(o => o.OperationId == operationId);
    }

    private sealed class DiscoverFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubIndex Index { get; } = new();

        public DiscoverOperationsHandler Build() => new(
            new AiApiSpecRepository(Db),
            Index,
            new FakeUser(),
            NullLogger<DiscoverOperationsHandler>.Instance);

        public void SeedOperations(int count, string api = "netbox")
        {
            for (var i = 0; i < count; i++)
                Index.Operations.Add(new ApiOperation
                {
                    OperationId = $"{api}:op{i}",
                    Api = api,
                    Method = "GET",
                    Path = $"/things/{i}",
                    Summary = $"thing {i}",
                });
        }

        public void SeedSpec(string api, string content)
        {
            Db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                Content = content,
                IsActive = true,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Discover_ReturnsTheCompactShapeByDefault()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(2);

        var result = await f.Build().ExecuteAsync(Args("""{"keyword":"thing"}"""), default);

        Assert.Equal(2, result.GetProperty("count").GetInt32());
        Assert.False(result.GetProperty("details_inlined").GetBoolean());
        var first = result.GetProperty("operations").EnumerateArray().First();
        Assert.Equal("netbox:op0", first.GetProperty("operation_id").GetString());
        // The compact shape deliberately carries no schema detail.
        Assert.False(first.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task Discover_ForwardsTheApiAndMethodFilters()
    {
        using var f = new DiscoverFixture();

        await f.Build().ExecuteAsync(
            Args("""{"keyword":"device","api":"netbox","method":"POST"}"""), default);

        var search = Assert.Single(f.Index.Searches);
        Assert.Equal("device", search.Keyword);
        Assert.Equal("netbox", search.Api);
        Assert.Equal("POST", search.Method);
    }

    // No keyword means "list everything", not a failure.
    [Fact]
    public async Task Discover_AnAbsentKeywordSearchesForTheEmptyString()
    {
        using var f = new DiscoverFixture();

        await f.Build().ExecuteAsync(Args("{}"), default);

        Assert.Equal("", Assert.Single(f.Index.Searches).Keyword);
    }

    // The limit is clamped into [1,100] so a bad model-supplied value can't
    // blow the chat token budget or return nothing.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 100)]
    public async Task Discover_TheLimitIsClamped(int requested, int expected)
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(120);

        var result = await f.Build().ExecuteAsync(
            Args("{\"keyword\":\"t\",\"limit\":" + requested + "}"), default);

        Assert.Equal(expected, result.GetProperty("count").GetInt32());
    }

    // Asking for details on a large result set is refused with a reason the
    // model can act on — otherwise it re-calls operation_detail anyway and
    // wastes a round-trip.
    [Fact]
    public async Task Discover_DetailsAreSkippedAboveTheInlineCapWithAnExplanation()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(60);

        var result = await f.Build().ExecuteAsync(
            Args("""{"keyword":"t","include_details":true}"""), default);

        Assert.False(result.GetProperty("details_inlined").GetBoolean());
        Assert.Contains("narrow the search",
            result.GetProperty("details_skipped_reason").GetString());
    }

    [Fact]
    public async Task Discover_NoReasonIsAttachedWhenDetailsWereNotRequested()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(60);

        var result = await f.Build().ExecuteAsync(Args("""{"keyword":"t"}"""), default);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("details_skipped_reason").ValueKind);
    }

    [Fact]
    public async Task Discover_ASmallResultSetInlinesTheSchemaDetail()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(1);
        f.SeedSpec("netbox", """
            openapi: 3.0.0
            paths:
              /things/0:
                get:
                  summary: thing 0
                  parameters:
                    - name: brief
                      in: query
                      schema:
                        type: boolean
            """);

        var result = await f.Build().ExecuteAsync(
            Args("""{"keyword":"thing","include_details":true}"""), default);

        Assert.True(result.GetProperty("details_inlined").GetBoolean());
        var op = result.GetProperty("operations").EnumerateArray().Single();
        Assert.True(op.TryGetProperty("parameters", out _));
    }

    // Malformed YAML on one operation must not take down the whole result —
    // that op falls back to the compact view.
    [Fact]
    public async Task Discover_MalformedSpecYamlDegradesToTheCompactView()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(1);
        f.SeedSpec("netbox", "paths:\n  - this: [is not\n   valid yaml");

        var result = await f.Build().ExecuteAsync(
            Args("""{"keyword":"thing","include_details":true}"""), default);

        Assert.True(result.GetProperty("details_inlined").GetBoolean());
        var op = result.GetProperty("operations").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, op.GetProperty("parameters").ValueKind);
    }

    // An operation whose api has no stored spec still comes back, just
    // without detail.
    [Fact]
    public async Task Discover_AnOperationWithNoStoredSpecStillAppears()
    {
        using var f = new DiscoverFixture();
        f.SeedOperations(1);

        var result = await f.Build().ExecuteAsync(
            Args("""{"keyword":"thing","include_details":true}"""), default);

        Assert.Equal(1, result.GetProperty("count").GetInt32());
    }

    // ─── analyze_foreign_workflow ───────────────────────────────────────

    private sealed class StubDetector : IDslDetector
    {
        public StubDetector(string format, double score) { FormatName = format; Score = score; }
        public string FormatName { get; }
        public double Score { get; }
        public double Detect(JsonElement document) => Score;
    }

    private sealed class StubTranslator : IDslTranslator
    {
        private readonly TranslationResult _result;
        public StubTranslator(string format, TranslationResult? result = null)
        {
            FormatName = format;
            _result = result ?? new TranslationResult
            {
                V1Workflow = TestJson.Element("""{"name":"translated","nodes":[],"edges":[]}"""),
            };
        }
        public string FormatName { get; }
        public Task<TranslationResult> TranslateAsync(JsonElement document, CancellationToken ct)
            => Task.FromResult(_result);
    }

    private sealed class EmptyScopes : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        public IServiceScope CreateScope() => _provider.CreateScope();
    }

    private sealed class AnalyzeFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ImportDraftCache Cache { get; } = new();
        public FakeUser Caller { get; } = new();
        public List<IDslDetector> Detectors { get; } = new();
        public List<IDslTranslator> Translators { get; } = new();

        public AnalyzeForeignWorkflowHandler Build()
            => new(BuildPipeline(), Cache, Caller, NullLogger<AnalyzeForeignWorkflowHandler>.Instance);

        private WorkflowImportPipeline BuildPipeline() => new(
            Detectors,
            Translators,
            new DependencyResolver(
                new SnippetRepository(Db), new IntegrationRepository(Db),
                new VendorCommandRepository(Db), Caller),
            new ConflictDetector(new WorkflowRepository(Db), Caller),
            new WorkflowRollbackAnalyzer(
                new SnippetRepository(Db), Array.Empty<ISnippetHandler>()),
            null!,
            new EmptyScopes(),
            Caller,
            NullLogger<WorkflowImportPipeline>.Instance);

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("{}", "raw_text is required")]
    [InlineData("""{"raw_text":123}""", "raw_text is required")]
    [InlineData("""{"raw_text":""}""", "raw_text is empty")]
    [InlineData("""{"raw_text":"   "}""", "raw_text is empty")]
    public async Task Analyze_BadArgumentsComeBackAsAnErrorPayload(string args, string expected)
    {
        using var f = new AnalyzeFixture();

        var result = await f.Build().ExecuteAsync(Args(args), default);

        Assert.Equal(expected, Error(result));
    }

    // The chat tool has a tighter size limit than the wizard, and the error
    // points at the endpoint that can take the bigger payload.
    [Fact]
    public async Task Analyze_AnOversizedPayloadPointsAtTheWizardEndpoint()
    {
        using var f = new AnalyzeFixture();
        var huge = new string('x', 200 * 1024);

        var result = await f.Build().ExecuteAsync(
            Args("{\"raw_text\":" + JsonSerializer.Serialize(huge) + "}"), default);

        Assert.Contains("workflow/import/analyze", Error(result));
    }

    [Fact]
    public async Task Analyze_ReturnsASlimReportAndDropsTheDraft()
    {
        using var f = new AnalyzeFixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"python_thing"}],"edges":[]}"""),
            Notes = new[] { "mapped 1 task" },
            Warnings = new[] { "retry dropped" },
        }));

        var result = await f.Build().ExecuteAsync(
            Args("""{"raw_text":"{\"tasks\":{}}"}"""), default);

        Assert.Null(Error(result));
        Assert.Equal("itential", result.GetProperty("format_detected").GetString());
        Assert.Equal(1, result.GetProperty("node_count").GetInt32());
        Assert.Equal(0, result.GetProperty("edge_count").GetInt32());
        Assert.Contains("mapped 1 task",
            result.GetProperty("translation_notes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("python_thing",
            result.GetProperty("missing_snippets").EnumerateArray().Single()
                .GetProperty("id").GetString());
        // The full workflow body is deliberately left out to keep the chat
        // token budget tight.
        Assert.False(result.TryGetProperty("proposed_workflow", out _));
    }

    // The draft is a chat-scoped scratch object: it must not be left behind
    // in the cache for the wizard to stumble on.
    [Fact]
    public async Task Analyze_TheScratchDraftIsAlwaysDeleted()
    {
        using var f = new AnalyzeFixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential"));

        await f.Build().ExecuteAsync(Args("""{"raw_text":"{}"}"""), default);

        // Nothing the cache still hands out for this user.
        Assert.Null(f.Cache.Get(Guid.NewGuid(), f.Caller.UserId));
    }

    // A pipeline that fails surfaces its reason to the model rather than
    // throwing out of the tool call.
    [Fact]
    public async Task Analyze_APipelineFailureBecomesAnErrorPayload()
    {
        using var f = new AnalyzeFixture();
        // No translators registered at all: ResolveTranslator throws, the
        // pipeline catches it and marks the draft failed.
        var result = await f.Build().ExecuteAsync(Args("""{"raw_text":"{}"}"""), default);

        Assert.NotNull(Error(result));
    }

    [Fact]
    public async Task Analyze_AnUnparseableUploadBecomesAnErrorPayload()
    {
        using var f = new AnalyzeFixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential"));

        var result = await f.Build().ExecuteAsync(
            Args("""{"raw_text":"{ broken: [json"}"""), default);

        Assert.Contains("Pipeline failed", Error(result));
    }

    // The format hint is honoured the same way the wizard honours it.
    [Fact]
    public async Task Analyze_TheFormatHintReachesTheRouter()
    {
        using var f = new AnalyzeFixture();
        f.Detectors.Add(new StubDetector("itential", 0.2));
        f.Detectors.Add(new StubDetector("n8n", 0.9));
        f.Translators.Add(new StubTranslator("itential"));
        f.Translators.Add(new StubTranslator("n8n"));

        var result = await f.Build().ExecuteAsync(
            Args("""{"raw_text":"{}","format_hint":"itential"}"""), default);

        Assert.Equal("itential", result.GetProperty("format_detected").GetString());
    }
}

