using System.Text;
using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using flow_weaver_backend.Utils.Report;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The workflow-step counterpart of the agent's generate_report tool. The gate
// that earns its keep is the residual-template scan: WorkflowExecutor resolves
// `{{ ... }}` before the step runs, but if this handler is ever reached from a
// path that skipped resolution the exporter would cheerfully write the literal
// template into a PDF the user then emails to someone. Failing loudly beats
// shipping a document full of `{{ steps.x.output }}`.
public class ReportHandlerTests
{

    private sealed class StubReportService : IReportService
    {
        public byte[] RawBytes { get; set; } = Encoding.UTF8.GetBytes("%PDF-1.4 fake");
        public Exception? ThrowOnGenerate { get; set; }
        public GenerateReportRequest? RequestSeen { get; private set; }
        public string? SourceSeen { get; private set; }
        public ReportGenerationContext? ContextSeen { get; private set; }

        public Task<ReportArtifact> GenerateAndPersistAsync(
            GenerateReportRequest request, string source, ReportGenerationContext context, CancellationToken ct)
        {
            RequestSeen = request;
            SourceSeen = source;
            ContextSeen = context;
            if (ThrowOnGenerate is not null) throw ThrowOnGenerate;
            return Task.FromResult(new ReportArtifact
            {
                ReportArtifactId = Guid.NewGuid(),
                Title = request.Document.Title,
                Filename = "device-audit-20260727120000.pdf",
                ContentType = "application/pdf",
                Format = request.Format,
                SizeBytes = RawBytes.Length,
                StoredBytes = RawBytes.Length,
                ContentBytes = RawBytes,
                Sha256 = "abc123",
                Source = source,
            });
        }

        public byte[] Decompress(ReportArtifact artifact) => RawBytes;

        public Task<byte[]?> LoadContentAsync(
            Guid reportArtifactId, Guid? requestingUserId, bool requesterIsAdmin, CancellationToken ct)
            => Task.FromResult<byte[]?>(RawBytes);
    }

    private static (ReportHandler Handler, StubReportService Reports) Build(
        flow_weaver_backend.Data.Db.AppDbContext? db = null)
    {
        var reports = new StubReportService();
        // Default: empty run repo — the origin lookup resolves to null. Tests
        // exercising run attribution pass a context with a seeded run row.
        var runs = new flow_weaver_backend.Data.Repositories.WorkflowRunRepository(db ?? TestDb.NewContext());
        return (new ReportHandler(reports, runs, NullLogger<ReportHandler>.Instance), reports);
    }

    // Seeds a run row so the handler's origin lookup finds workflow + creator.
    private static (flow_weaver_backend.Data.Db.AppDbContext Db, Guid RunId, Guid WorkflowId) SeedRun(
        string? createdBy)
    {
        var db = TestDb.NewContext();
        var runId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            Status = "completed",
            Trigger = "manual",
            CreatedBy = createdBy,
            InputPayload = TestJson.Element("{}"),
            TargetDevices = new List<Guid>(),
            TargetPools = new List<Guid>(),
            IsActive = true,
        });
        db.SaveChanges();
        return (db, runId, workflowId);
    }

    private static SnippetRequest Request(string input, Guid? runId = null)
        => new()
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = runId ?? Guid.NewGuid(),
            NodeId = "report-node",
            SnippetId = Guid.NewGuid(),
            SnippetType = "report",
            InputPayload = TestJson.Element(input),
            DeviceId = null,
        };

    private const string ValidInput = """
        {"format":"pdf","document":{"title":"Device Audit","sections":[]}}
        """;

    // ─── input validation ───────────────────────────────────────────────

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public async Task NonObjectInputIsRejected(string input)
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("must be an object", result.Error);
    }

    // No default format: a PDF and a CSV behave very differently downstream,
    // so guessing is worse than erroring.
    [Theory]
    [InlineData("""{"document":{"title":"t"}}""")]
    [InlineData("""{"format":123,"document":{"title":"t"}}""")]
    [InlineData("""{"format":null,"document":{"title":"t"}}""")]
    public async Task MissingOrNonStringFormatIsRejected(string input)
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("input.format is required", result.Error);
    }

    [Theory]
    [InlineData("""{"format":"pdf"}""")]
    [InlineData("""{"format":"pdf","document":"not an object"}""")]
    [InlineData("""{"format":"pdf","document":[]}""")]
    public async Task MissingOrNonObjectDocumentIsRejected(string input)
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("input.document is required", result.Error);
    }

    // ─── the residual-template gate ─────────────────────────────────────

    // A literal `{{ ... }}` reaching the exporter means the user gets a
    // document full of template syntax — usually by email. Fail instead.
    [Fact]
    public async Task UnresolvedTemplatesAbortTheReport()
    {
        var (handler, reports) = Build();
        var input = """
            {"format":"pdf","document":{"title":"{{ steps.lookup.output.name }}","sections":[]}}
            """;

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("unresolved", result.Error);
        Assert.Null(reports.RequestSeen);   // never reached the exporter
    }

    // The error names the offending location so the author can fix the right
    // field instead of hunting.
    [Fact]
    public async Task TheErrorPointsAtEachOffendingTemplate()
    {
        var (handler, _) = Build();
        var input = """
            {"format":"pdf","document":{"title":"ok","subtitle":"{{ steps.a.output.x }}"}}
            """;

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.Contains("{{ steps.a.output.x }}", result.Error);
    }

    // A long list is truncated so the error stays readable.
    [Fact]
    public async Task ManyResidualsAreSummarised()
    {
        var (handler, _) = Build();
        var sections = string.Join(",", Enumerable.Range(0, 8)
            .Select(i => "{\"title\":\"{{ steps.s" + i + ".output.v }}\"}"));
        var input = "{\"format\":\"pdf\",\"document\":{\"title\":\"t\",\"sections\":["
                    + sections + "]}}";

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("more", result.Error);
    }

    // A document with no templates passes the gate.
    [Fact]
    public async Task CleanDocumentPassesTheGate()
    {
        var (handler, reports) = Build();

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.True(result.Success);
        Assert.NotNull(reports.RequestSeen);
    }

    // ─── document parsing ───────────────────────────────────────────────

    [Fact]
    public async Task DocumentIsDeserialisedCaseInsensitively()
    {
        var (handler, reports) = Build();
        var input = """
            {"format":"csv","document":{"Title":"Device Audit","Subtitle":"weekly"}}
            """;

        await handler.ExecuteAsync(Request(input), default);

        Assert.Equal("Device Audit", reports.RequestSeen!.Document.Title);
        Assert.Equal("weekly", reports.RequestSeen.Document.Subtitle);
    }

    [Fact]
    public async Task FormatIsPassedThroughVerbatim()
    {
        var (handler, reports) = Build();

        await handler.ExecuteAsync(Request("""{"format":"xlsx","document":{"title":"t"}}"""), default);

        Assert.Equal("xlsx", reports.RequestSeen!.Format);
    }

    // A document whose shape can't bind must fail as a step error, not a crash.
    [Fact]
    public async Task UnparseableDocumentIsAStepFailure()
    {
        var (handler, _) = Build();
        var input = """
            {"format":"pdf","document":{"title":"t","sections":"not-an-array"}}
            """;

        var result = await handler.ExecuteAsync(Request(input), default);

        Assert.False(result.Success);
        Assert.Contains("could not parse document", result.Error);
    }

    // ─── origin stamping ────────────────────────────────────────────────

    // The worker runs the handler outside any HTTP request, so the execution
    // must not depend on an ambient request context.
    [Fact]
    public async Task TheJobExecutesOutsideAnHttpRequest()
    {
        var (handler, reports) = Build();

        await handler.ExecuteAsync(Request(ValidInput), default);

    }

    // The origin is what lets /admin/reports separate workflow output from
    // agent output, and cross-link back to the run.
    [Fact]
    public async Task OriginIsStampedAsWorkflowWithTheRunId()
    {
        var (handler, reports) = Build();
        var runId = Guid.NewGuid();

        await handler.ExecuteAsync(Request(ValidInput, runId: runId), default);

        Assert.Equal("workflow", reports.SourceSeen);
        Assert.Equal(runId, reports.ContextSeen!.WorkflowRunId);
    }

    // The run stores who triggered it (WorkflowExecutor writes the user's
    // Guid into CreatedBy); the artifact inherits that attribution so the
    // admin artifacts view can answer "who generated this document".
    [Fact]
    public async Task TheRunCreatorIsAttributedToAWorkflowReport()
    {
        var creator = Guid.NewGuid();
        var (db, runId, workflowId) = SeedRun(creator.ToString());
        var (handler, reports) = Build(db);

        await handler.ExecuteAsync(Request(ValidInput, runId: runId), default);

        Assert.Equal(creator, reports.ContextSeen!.UserId);
        Assert.Equal(workflowId, reports.ContextSeen.WorkflowId);
    }

    // Legacy/system rows whose CreatedBy is not a user Guid stay unattributed
    // rather than crashing or inventing a user.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task NonUserCreatorsLeaveTheReportUnattributed(string? createdBy)
    {
        var (db, runId, _) = SeedRun(createdBy);
        var (handler, reports) = Build(db);

        await handler.ExecuteAsync(Request(ValidInput, runId: runId), default);

        Assert.Null(reports.ContextSeen!.UserId);
    }

    // A missing run row (e.g. purged by retention mid-flight) must not block
    // the report — it just ships without attribution.
    [Fact]
    public async Task AMissingRunRowLeavesTheReportUnattributed()
    {
        var (handler, reports) = Build();

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.True(result.Success);
        Assert.Null(reports.ContextSeen!.UserId);
        Assert.Null(reports.ContextSeen.WorkflowId);
    }

    // ─── output contract ────────────────────────────────────────────────

    // Downstream nodes template against these keys — typically piping
    // `base64` into an email integration_action.
    [Fact]
    public async Task OutputCarriesTheArtifactCoordinatesAndBytes()
    {
        var (handler, reports) = Build();
        reports.RawBytes = Encoding.UTF8.GetBytes("REPORT-BYTES");

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.True(result.Success);
        var output = result.Output;
        Assert.NotEqual(Guid.Empty, output.GetProperty("report_artifact_id").GetGuid());
        Assert.Equal("device-audit-20260727120000.pdf", output.GetProperty("filename").GetString());
        Assert.Equal("application/pdf", output.GetProperty("content_type").GetString());
        Assert.Equal("pdf", output.GetProperty("format").GetString());
        Assert.Equal("abc123", output.GetProperty("sha256").GetString());
        Assert.Equal("REPORT-BYTES",
            Encoding.UTF8.GetString(Convert.FromBase64String(output.GetProperty("base64").GetString()!)));
    }

    // The download URL is what the chat UI links to for a one-click download.
    [Fact]
    public async Task OutputCarriesAResolvableDownloadUrl()
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        var id = result.Output.GetProperty("report_artifact_id").GetGuid();
        Assert.Equal($"/api/reports/{id}/download", result.Output.GetProperty("download_url").GetString());
    }

    // The bytes are decompressed on the way out — downstream wants the real
    // file, not the gzipped blob that was persisted.
    [Fact]
    public async Task OutputBytesAreDecompressed()
    {
        var (handler, reports) = Build();
        reports.RawBytes = Encoding.UTF8.GetBytes("plain content");

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.Equal("plain content",
            Encoding.UTF8.GetString(Convert.FromBase64String(result.Output.GetProperty("base64").GetString()!)));
    }

    [Fact]
    public async Task LogsSummariseTheGeneratedArtifact()
    {
        var (handler, _) = Build();

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.Contains("report.generated", result.Logs);
        Assert.Contains("format=pdf", result.Logs);
    }

    // ─── service-level failures ─────────────────────────────────────────

    // Validation errors from ReportService (unknown format, missing title, too
    // many rows) are user-facing, not crash-worthy.
    [Fact]
    public async Task ServiceValidationErrorBecomesAStepFailure()
    {
        var (handler, reports) = Build();
        reports.ThrowOnGenerate = new InvalidOperationException("unsupported format 'docx'");

        var result = await handler.ExecuteAsync(Request(ValidInput), default);

        Assert.False(result.Success);
        Assert.Equal("unsupported format 'docx'", result.Error);
    }

    // An unexpected exception is NOT swallowed — the worker's retry/fail
    // handling should see it rather than the step reporting a tidy error.
    [Fact]
    public async Task UnexpectedExceptionPropagates()
    {
        var (handler, reports) = Build();
        reports.ThrowOnGenerate = new InvalidDataException("disk exploded");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => handler.ExecuteAsync(Request(ValidInput), default));
    }

    // ─── metadata ───────────────────────────────────────────────────────

    [Fact]
    public void HandlerDeclaresItsTypeAndIdempotency()
    {
        var (handler, _) = Build();

        Assert.Equal("report", handler.Type);
        // Re-running only mints another artifact that retention
        // reaps, so a replay is cheap and safe.
        Assert.Equal(IdempotencyKind.Idempotent, handler.DefaultIdempotency);
    }
}
