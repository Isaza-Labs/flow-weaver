using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Detectors;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Import.Translators;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AiAgentModel = flow_weaver_backend.Models.AIAgent;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The import pipeline's orchestration half: format routing, translator
// resolution, rollback projection and the end-to-end RunAsync state machine.
// The pure helpers (ParseUpload, routing notes, draft gating) live in
// WorkflowImportPipelineParseTests; this suite drives the collaborators.
//
// The behaviours that matter here are all about *never losing the draft*: the
// pipeline runs detached from the request (Task.Run), so a failure anywhere has
// to land on the draft as a Failed status rather than an unobserved exception,
// and the AI pre-draft step must fail open — an install with no LLM provider
// still gets a usable report.
public class WorkflowImportPipelineRunTests
{

    // ─── Test doubles ───────────────────────────────────────────────────

    private sealed class StubDetector : IDslDetector
    {
        private readonly double _score;
        public StubDetector(string format, double score) { FormatName = format; _score = score; }
        public string FormatName { get; }
        public double Detect(JsonElement document) => _score;
    }

    // A detector that blows up — the pipeline must not let one broken
    // detector take the whole analysis down silently.
    private sealed class ThrowingDetector : IDslDetector
    {
        public string FormatName => "explodes";
        public double Detect(JsonElement document) => throw new InvalidOperationException("detector boom");
    }

    private sealed class StubTranslator : IDslTranslator
    {
        private readonly TranslationResult? _result;
        private readonly Exception? _throw;

        public StubTranslator(string format, TranslationResult? result = null, Exception? boom = null)
        {
            FormatName = format;
            _result = result;
            _throw = boom;
        }

        public string FormatName { get; }
        public int Calls { get; private set; }

        public Task<TranslationResult> TranslateAsync(JsonElement document, CancellationToken ct)
        {
            Calls++;
            if (_throw is not null) throw _throw;
            return Task.FromResult(_result ?? new TranslationResult
            {
                V1Workflow = TestJson.Element("""{"name":"translated","nodes":[],"edges":[]}"""),
            });
        }
    }

    // ─── Fixture ────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeUser Caller { get; } = new();
        public List<IDslDetector> Detectors { get; } = new();
        public List<IDslTranslator> Translators { get; } = new();

        // Defaults to "no AI provider configured" — the fail-open path.
        public FakeHttpMessageHandler Http { get; set; } =
            new(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}");

        public WorkflowImportPipeline Build()
        {
            var handler = BuildSnippetHandler();
            return new WorkflowImportPipeline(
                Detectors,
                Translators,
                new DependencyResolver(
                    new SnippetRepository(Db),
                    new IntegrationRepository(Db),
                    new VendorCommandRepository(Db),
                    Caller),
                new ConflictDetector(new WorkflowRepository(Db), Caller),
                new WorkflowRollbackAnalyzer(
                    new SnippetRepository(Db), Array.Empty<ISnippetHandler>()),
                handler,
                new StubScopeFactory(Caller, handler),
                Caller,
                NullLogger<WorkflowImportPipeline>.Instance);
        }

        public GenerateSnippetForImportHandler BuildSnippetHandler()
            => new(
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

        public void SeedProvider()
        {
            Db.AIProviders.Add(new AIProvider
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
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    // DraftOneAsync resolves a fresh scope per parallel draft. Production wires
    // this to the real DI container; here it hands back the same two services
    // the method asks for.
    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;

        public StubScopeFactory(ICurrentUser caller, GenerateSnippetForImportHandler handler)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<MutableCurrentUser>();
            services.AddSingleton(handler);
            _provider = services.BuildServiceProvider();
        }

        public IServiceScope CreateScope() => _provider.CreateScope();
    }

    private static ImportDraft Draft(string body, string hint = "")
        => new(Guid.NewGuid(), hint, Encoding.UTF8.GetBytes(body));

    // ─── ChooseFormat ───────────────────────────────────────────────────

    private static (string Format, double Confidence) ChooseFormat(
        WorkflowImportPipeline pipeline, string json, string? hint)
    {
        var method = typeof(WorkflowImportPipeline).GetMethod(
            "ChooseFormat",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var tuple = method.Invoke(pipeline, new object?[] { TestJson.Element(json), hint })!;
        var type = tuple.GetType();
        return ((string)type.GetField("Item1")!.GetValue(tuple)!,
                (double)type.GetField("Item2")!.GetValue(tuple)!);
    }

    // With no hint the highest positive score wins outright.
    [Fact]
    public void ChooseFormat_AutoDetectPicksTheHighestScorer()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("n8n", 0.4));
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Detectors.Add(new StubDetector("flow_weaver_v1", 0.1));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", null);

        Assert.Equal("itential", format);
        Assert.Equal(0.9, confidence);
    }

    // Zero-scoring detectors are not candidates at all.
    [Fact]
    public void ChooseFormat_ZeroScoresAreNotCandidates()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("n8n", 0));
        f.Detectors.Add(new StubDetector("itential", 0));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", null);

        Assert.Equal("unknown", format);
        Assert.Equal(0.0, confidence);
    }

    // An explicit hint the user typed is trusted outright — score 1.0, no
    // detector run needed.
    [Fact]
    public void ChooseFormat_AnExplicitHintForcesThatFormatAtFullConfidence()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("n8n", 0.05));
        f.Detectors.Add(new StubDetector("itential", 0.99));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", "n8n");

        Assert.Equal("n8n", format);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void ChooseFormat_HintMatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("n8n", 0.05));

        Assert.Equal("n8n", ChooseFormat(f.Build(), "{}", "N8N").Format);
    }

    // A hint naming a format with no registered detector cannot be honoured;
    // the pipeline falls back to auto-detection rather than routing to a
    // translator that does not exist.
    [Fact]
    public void ChooseFormat_AnUnknownHintFallsBackToAutoDetection()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.6));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", "made_up_dsl");

        Assert.Equal("itential", format);
        Assert.Equal(0.6, confidence);
    }

    // "generic_dag" is a routing request, not a forced format: a confident
    // deterministic detector still wins because deterministic translation is
    // cheaper and more predictable than the LLM path.
    [Fact]
    public void ChooseFormat_GenericDagHintYieldsToAConfidentDetector()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Detectors.Add(new StubDetector("generic_dag", 1.0));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", "generic_dag");

        Assert.Equal("itential", format);
        Assert.Equal(0.9, confidence);
    }

    // The override threshold is 0.85 and inclusive.
    [Fact]
    public void ChooseFormat_GenericDagOverrideThresholdIsInclusive()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.85));

        Assert.Equal("itential", ChooseFormat(f.Build(), "{}", "generic_dag").Format);
    }

    // Below the threshold the user's explicit generic_dag choice is honoured
    // and the AgentTranslator takes over.
    [Fact]
    public void ChooseFormat_ALukewarmMatchKeepsTheGenericDagHint()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.84));

        var (format, confidence) = ChooseFormat(f.Build(), "{}", "generic_dag");

        Assert.Equal("generic_dag", format);
        Assert.Equal(1.0, confidence);
    }

    // The generic_dag detector must never be ranked against the others — it
    // is the fallback, not a competitor.
    [Fact]
    public void ChooseFormat_TheGenericDagDetectorIsExcludedFromRanking()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("generic_dag", 1.0));
        f.Detectors.Add(new StubDetector("itential", 0.2));

        Assert.Equal("itential", ChooseFormat(f.Build(), "{}", null).Format);
    }

    [Fact]
    public void ChooseFormat_NoDetectorsAtAllMeansUnknown()
    {
        using var f = new Fixture();

        Assert.Equal("unknown", ChooseFormat(f.Build(), "{}", null).Format);
    }

    // ─── ResolveTranslator ──────────────────────────────────────────────

    private static IDslTranslator ResolveTranslator(WorkflowImportPipeline pipeline, string format)
        => (IDslTranslator)typeof(WorkflowImportPipeline).GetMethod(
            "ResolveTranslator",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(pipeline, new object?[] { format })!;

    [Fact]
    public void ResolveTranslator_PrefersTheSpecificTranslator()
    {
        using var f = new Fixture();
        f.Translators.Add(new StubTranslator("generic_dag"));
        f.Translators.Add(new StubTranslator("itential"));

        Assert.Equal("itential", ResolveTranslator(f.Build(), "itential").FormatName);
    }

    [Fact]
    public void ResolveTranslator_MatchingIsCaseInsensitive()
    {
        using var f = new Fixture();
        f.Translators.Add(new StubTranslator("generic_dag"));
        f.Translators.Add(new StubTranslator("itential"));

        Assert.Equal("itential", ResolveTranslator(f.Build(), "ITENTIAL").FormatName);
    }

    // "unknown" — nothing recognised the shape — routes to the LLM translator.
    [Fact]
    public void ResolveTranslator_UnknownFormatsFallBackToTheAgent()
    {
        using var f = new Fixture();
        f.Translators.Add(new StubTranslator("itential"));
        f.Translators.Add(new StubTranslator("generic_dag"));

        Assert.Equal("generic_dag", ResolveTranslator(f.Build(), "unknown").FormatName);
    }

    // Without a generic_dag translator registered there is nothing to fall
    // back to; the throw is caught by RunAsync and surfaces as a failed draft
    // (asserted below) rather than being swallowed.
    [Fact]
    public void ResolveTranslator_ThrowsWhenTheAgentFallbackIsMissing()
    {
        using var f = new Fixture();
        f.Translators.Add(new StubTranslator("itential"));

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => ResolveTranslator(f.Build(), "unknown"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    // ─── AnalyzeRollbackRiskAsync ───────────────────────────────────────

    private static async Task<RollbackRiskReport> AnalyzeRollback(
        WorkflowImportPipeline pipeline, string v1)
    {
        var task = (Task<RollbackRiskReport>)typeof(WorkflowImportPipeline).GetMethod(
            "AnalyzeRollbackRiskAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(pipeline, new object?[] { TestJson.Element(v1), CancellationToken.None })!;
        return await task;
    }

    [Fact]
    public async Task Rollback_AGraphWithNoNodesIsRiskFree()
    {
        using var f = new Fixture();

        var report = await AnalyzeRollback(f.Build(), """{"nodes":[],"edges":[]}""");

        Assert.Empty(report.NonReversible);
        Assert.Empty(report.RequiresCompensation);
        Assert.Empty(report.Compensated);
    }

    // A translated document may not carry `nodes`/`edges` at all (a translator
    // that only emitted metadata). The projection has to tolerate that instead
    // of throwing mid-pipeline.
    [Fact]
    public async Task Rollback_MissingNodesAndEdgesAreTolerated()
    {
        using var f = new Fixture();

        var report = await AnalyzeRollback(f.Build(), """{"name":"no graph"}""");

        Assert.Empty(report.NonReversible);
    }

    // A snippet with no matching handler defaults to RequiresCompensation, and
    // with no failure edge it lands in the blocking bucket.
    [Fact]
    public async Task Rollback_AnUncompensatedNodeIsReported()
    {
        using var f = new Fixture();
        var snippetId = Guid.NewGuid();
        f.Db.Snippets.Add(new SnippetModel
        {
            SnippetId = snippetId,
            Name = "delete-vlan",
            Type = "ssh",
            IsActive = true,
        });
        f.Db.SaveChanges();

        var v1 = "{\"nodes\":[{\"id\":\"n1\",\"snippet_id\":\"" + snippetId + "\"}],\"edges\":[]}";
        var report = await AnalyzeRollback(f.Build(), v1);

        Assert.Equal("delete-vlan", Assert.Single(report.RequiresCompensation).SnippetName);
        Assert.Empty(report.Compensated);
    }

    // The same node with a failure edge moves to the "compensated" bucket —
    // the wizard shows it as a warning rather than a blocker.
    [Fact]
    public async Task Rollback_AFailureEdgeMovesTheNodeToCompensated()
    {
        using var f = new Fixture();
        var snippetId = Guid.NewGuid();
        f.Db.Snippets.Add(new SnippetModel
        {
            SnippetId = snippetId,
            Name = "delete-vlan",
            Type = "ssh",
            IsActive = true,
        });
        f.Db.SaveChanges();

        var v1 = "{\"nodes\":[{\"id\":\"n1\",\"snippet_id\":\"" + snippetId + "\"},"
               + "{\"id\":\"n2\",\"snippet_id\":\"" + Guid.Empty + "\"}],"
               + "\"edges\":[{\"source\":\"n1\",\"target\":\"n2\",\"type\":\"failure\"}]}";
        var report = await AnalyzeRollback(f.Build(), v1);

        Assert.Equal("delete-vlan", Assert.Single(report.Compensated).SnippetName);
        Assert.Empty(report.RequiresCompensation);
    }

    // ─── RunAsync — the happy path ──────────────────────────────────────

    [Fact]
    public async Task Run_ProducesAReadyDraftWithAFullReport()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.75));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element("""{"name":"imported","nodes":[],"edges":[]}"""),
            Notes = new[] { "mapped 3 tasks" },
            Warnings = new[] { "retry_policy dropped" },
        }));
        f.Translators.Add(new StubTranslator("generic_dag"));
        using var draft = Draft("""{"tasks":{}}""");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        Assert.NotNull(draft.Report);
        Assert.Equal("itential", draft.Report!.FormatDetected);
        Assert.Equal(0.75, draft.Report.Confidence);
        Assert.Equal("imported", draft.Report.ProposedWorkflow.GetProperty("name").GetString());
        Assert.Contains("mapped 3 tasks", draft.Report.TranslationNotes);
        Assert.Contains("retry_policy dropped", draft.Report.Warnings);
        Assert.Null(draft.Error);
    }

    // The translation is fed the parsed document, not the raw bytes — a YAML
    // upload has to reach the translator as JSON.
    [Fact]
    public async Task Run_TranslatesAYamlUploadThroughTheSameJsonPath()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("n8n", 0.5));
        var translator = new StubTranslator("n8n");
        f.Translators.Add(translator);
        f.Translators.Add(new StubTranslator("generic_dag"));
        using var draft = Draft("name: from-yaml\nnodes: []\n");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        Assert.Equal(1, translator.Calls);
    }

    // When the user's hint and the detection disagree the report carries the
    // routing note as the FIRST translation note, ahead of the translator's own.
    [Fact]
    public async Task Run_PrependsTheRoutingNoteWhenTheHintWasOverridden()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.95));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element("""{"name":"imported"}"""),
            Notes = new[] { "translator note" },
        }));
        f.Translators.Add(new StubTranslator("generic_dag"));
        using var draft = Draft("{}", hint: "generic_dag");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        var notes = draft.Report!.TranslationNotes;
        Assert.Equal(2, notes.Count);
        Assert.Contains("deterministic", notes[0]);
        Assert.Equal("translator note", notes[1]);
    }

    // A matching hint adds no note at all — nothing was overridden.
    [Fact]
    public async Task Run_AMatchingHintAddsNoRoutingNote()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.95));
        f.Translators.Add(new StubTranslator("itential"));
        using var draft = Draft("{}", hint: "itential");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Empty(draft.Report!.TranslationNotes);
    }

    // A name that already exists in the draft environment must surface as a
    // conflict so the wizard can offer "replace" vs "new version".
    [Fact]
    public async Task Run_ReportsANameCollisionAgainstAnExistingWorkflow()
    {
        using var f = new Fixture();
        f.Db.Workflows.Add(new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = "imported",
            Environment = "draft",
            Version = 3,
            IsActive = true,
            Nodes = TestJson.Element("[]"),
            Edges = TestJson.Element("[]"),
        });
        f.Db.SaveChanges();

        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element("""{"name":"imported","nodes":[],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        Assert.NotNull(draft.Report!.Conflicts.NameCollision);
        Assert.Equal(3, draft.Report.Conflicts.NameCollision!.MatchingWorkflowVersion);
    }

    // Every stage pushes a progress event onto the draft's channel; the SSE
    // subscriber renders them as the wizard's step list, so losing them makes
    // the analysis look frozen.
    [Fact]
    public async Task Run_EmitsProgressEventsForEveryStage()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential"));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        var events = new List<DraftEvent>();
        while (draft.Events.TryRead(out var e)) events.Add(e);

        Assert.Contains(events, e => e.Status == ImportDraftStatus.Analyzing);
        Assert.Contains(events, e => e.Message is not null && e.Message.Contains("Detecting format"));
        Assert.Contains(events, e => e.Message is not null && e.Message.Contains("Resolving dependencies"));
        Assert.Contains(events, e => e.Message is not null && e.Message.Contains("Detecting conflicts"));
        Assert.Contains(events, e => e.Message is not null && e.Message.Contains("rollback"));
        Assert.Equal(ImportDraftStatus.Ready, events[^1].Status);
    }

    // ─── RunAsync — failure containment ─────────────────────────────────

    // The pipeline runs detached; an unparseable upload must land as a failed
    // draft the wizard can render, never as an unobserved task exception.
    [Fact]
    public async Task Run_AnUnparseableUploadFailsTheDraftInsteadOfThrowing()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential"));
        using var draft = Draft("{ this is not: [valid json or yaml");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Failed, draft.Status);
        Assert.StartsWith("Pipeline failed:", draft.Error);
    }

    [Fact]
    public async Task Run_ATranslatorCrashFailsTheDraft()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", boom: new InvalidOperationException("translator boom")));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Failed, draft.Status);
        Assert.Contains("translator boom", draft.Error);
    }

    [Fact]
    public async Task Run_ADetectorCrashFailsTheDraft()
    {
        using var f = new Fixture();
        f.Detectors.Add(new ThrowingDetector());
        f.Translators.Add(new StubTranslator("generic_dag"));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Failed, draft.Status);
        Assert.Contains("detector boom", draft.Error);
    }

    // No generic_dag translator registered and nothing detected → the
    // ResolveTranslator throw has to be contained the same way.
    [Fact]
    public async Task Run_AMissingFallbackTranslatorFailsTheDraft()
    {
        using var f = new Fixture();
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Failed, draft.Status);
    }

    // A failed draft still emits a terminal event so the SSE subscriber
    // closes the stream instead of hanging on "Analyzing...".
    [Fact]
    public async Task Run_AFailureStillEmitsATerminalEvent()
    {
        using var f = new Fixture();
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        var events = new List<DraftEvent>();
        while (draft.Events.TryRead(out var e)) events.Add(e);

        Assert.Equal(ImportDraftStatus.Failed, events[^1].Status);
        Assert.NotNull(events[^1].Error);
    }

    // ─── RunAsync — the AI pre-draft step ───────────────────────────────

    // A workflow referencing an unknown python snippet produces a missing
    // entry. With no AI provider configured the pre-draft step fails open:
    // the entry survives as a plain missing snippet so the wizard's manual
    // "Generate with AI" button still has somewhere to land.
    [Fact]
    public async Task Run_PreDraftFailsOpenWithNoProviderConfigured()
    {
        using var f = new Fixture();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"python_transform_devices"}],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        var missing = Assert.Single(draft.Report!.MissingDependencies.Snippets);
        Assert.Equal("python_transform_devices", missing.IdInImport);
        Assert.Null(missing.PregeneratedSnippet);
    }

    // With a provider configured and the LLM returning a usable body, the
    // entry comes back pre-filled — the whole point of the phase-2 step.
    [Fact]
    public async Task Run_PreDraftAttachesTheGeneratedBodyWhenTheLlmAnswers()
    {
        using var f = new Fixture();
        f.Http = new FakeHttpMessageHandler(HttpStatusCode.OK, ChatResponse(
            """{"name":"transform_devices","type":"python_snippet","code":"return {}"}"""));
        f.SeedProvider();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"python_transform_devices"}],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        var missing = Assert.Single(draft.Report!.MissingDependencies.Snippets);
        Assert.NotNull(missing.PregeneratedSnippet);
        Assert.Equal("transform_devices",
            missing.PregeneratedSnippet!.Value.GetProperty("name").GetString());
        // The rest of the entry is preserved alongside the draft.
        Assert.Equal("python_transform_devices", missing.IdInImport);
    }

    // An LLM that errors out must not fail the whole analysis — the entry
    // simply stays undrafted.
    [Fact]
    public async Task Run_APreDraftFailureLeavesTheEntryUntouched()
    {
        using var f = new Fixture();
        f.Http = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");
        f.SeedProvider();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"python_transform_devices"}],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        Assert.Null(Assert.Single(draft.Report!.MissingDependencies.Snippets).PregeneratedSnippet);
    }

    // Non-code missing types (a vendor command, an integration action) are not
    // auto-draft candidates, so the LLM is never called for them.
    [Fact]
    public async Task Run_NonCodeMissingEntriesAreNotPreDrafted()
    {
        using var f = new Fixture();
        f.Http = new FakeHttpMessageHandler(HttpStatusCode.OK, ChatResponse("""{"name":"nope"}"""));
        f.SeedProvider();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"ssh_show_version"}],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        var missing = Assert.Single(draft.Report!.MissingDependencies.Snippets);
        Assert.Null(missing.PregeneratedSnippet);
        Assert.Equal("ssh", missing.InferredType);
    }

    // Several placeholders draft in parallel; each one gets its own DI scope,
    // so all of them have to come back filled rather than tripping EF's
    // "second operation on this context" guard.
    [Fact]
    public async Task Run_SeveralPlaceholdersDraftInParallelAndKeepTheirOrder()
    {
        using var f = new Fixture();
        f.Http = new FakeHttpMessageHandler(HttpStatusCode.OK, ChatResponse(
            """{"name":"drafted","type":"python_snippet","code":"return {}"}"""));
        f.SeedProvider();
        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element("""
                {"name":"imported","nodes":[
                  {"id":"n1","snippet_id":"python_alpha"},
                  {"id":"n2","snippet_id":"python_beta"},
                  {"id":"n3","snippet_id":"python_gamma"}
                ],"edges":[]}
                """),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        var missing = draft.Report!.MissingDependencies.Snippets;
        Assert.Equal(3, missing.Count);
        Assert.All(missing, m => Assert.NotNull(m.PregeneratedSnippet));
    }

    // A node referencing a snippet by GUID that exists in the catalogue is
    // resolved, so nothing is reported missing and nothing is drafted.
    [Fact]
    public async Task Run_AResolvedGuidReferenceProducesNoMissingEntry()
    {
        using var f = new Fixture();
        var snippetId = Guid.NewGuid();
        f.Db.Snippets.Add(new SnippetModel
        {
            SnippetId = snippetId,
            Name = "transform_devices",
            Type = "python_snippet",
            IsActive = true,
        });
        f.Db.SaveChanges();

        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                "{\"name\":\"imported\",\"nodes\":[{\"id\":\"n1\",\"snippet_id\":\""
                + snippetId + "\"}],\"edges\":[]}"),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        Assert.Equal(ImportDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Report!.MissingDependencies.Snippets);
    }

    // Only a GUID resolves. A human-readable id — what the agent translator
    // emits when it doesn't know the GUID — is ALWAYS reported missing, even
    // when a snippet of the same name exists; the match is offered as a
    // mapping candidate instead so the user confirms it explicitly.
    [Fact]
    public async Task Run_ANameMatchIsOfferedAsACandidateNotResolvedSilently()
    {
        using var f = new Fixture();
        f.Db.Snippets.Add(new SnippetModel
        {
            SnippetId = Guid.NewGuid(),
            Name = "python_transform_devices",
            Type = "python_snippet",
            IsActive = true,
        });
        f.Db.SaveChanges();

        f.Detectors.Add(new StubDetector("itential", 0.9));
        f.Translators.Add(new StubTranslator("itential", new TranslationResult
        {
            V1Workflow = TestJson.Element(
                """{"name":"imported","nodes":[{"id":"n1","snippet_id":"python_transform_devices"}],"edges":[]}"""),
        }));
        using var draft = Draft("{}");

        await f.Build().RunAsync(draft, CancellationToken.None);

        var missing = Assert.Single(draft.Report!.MissingDependencies.Snippets);
        Assert.Contains(missing.CandidatesForMapping,
            c => c.Name == "python_transform_devices" && c.Kind == "snippet");
    }

    private static string ChatResponse(string content)
        => "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content)
           + "}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}";
}
