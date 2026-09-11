using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Utils.Report;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Lets a workflow step generate a report (PDF / HTML / CSV / XLSX)
// exactly the same way the agent's `generate_report` tool does, except
// the origin is stamped as "workflow" instead of "agent" and we thread
// WorkflowRunId through so the admin artifacts view can cross-link back.
//
// Input shape (merged from config_overrides + run input by the executor):
//   {
//     "format": "pdf",              // one of html/csv/xlsx/docx/pdf/json
//     "document": { ... }           // matches ReportDocument schema
//   }
//
// Output (available to downstream nodes via {{ steps.X.output.Y }}):
//   {
//     "report_artifact_id": "<guid>",
//     "filename":           "title-yyyyMMddHHmmss.pdf",
//     "content_type":       "application/pdf",
//     "format":             "pdf",
//     "size_bytes":         12345,
//     "sha256":             "...",
//     "download_url":       "/api/reports/<guid>/download",
//     "base64":             "<raw file bytes base64>"
//   }
//
// The `base64` field is what downstream nodes (typically
// integration_action against an email integration) plug into their own
// payload via templating. The full blob sits in step_runs.OutputPayload
// until retention reclaims it — acceptable given the 10 MB artifact cap.
public sealed class ReportHandler : ISnippetHandler
{
    public string Type => "report";
    // Persisting an artifact is a side effect, but the artifact is
    // reaped by retention; cheap to re-run.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    private readonly IReportService _reports;
    private readonly Data.Repositories.IWorkflowRunRepository _runs;
    private readonly ILogger<ReportHandler> _logger;

    public ReportHandler(
        IReportService reports,
        Data.Repositories.IWorkflowRunRepository runs,
        ILogger<ReportHandler> logger)
    {
        _reports = reports;
        _runs = runs;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // `content` is a markdown string where this engine takes a structured
        // `document`; the normalizer wraps it in the equivalent one-section
        // document (snippets/SPEC.md `report`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);
        if (input.ValueKind != JsonValueKind.Object)
        {
            return Fail("input must be an object with `format` and `document` fields");
        }

        // Pull format + document out of the merged payload. Both are
        // required; we don't hide behind a default format because a PDF
        // vs CSV has very different downstream behavior and silently
        // picking one is worse than erroring.
        if (!input.TryGetProperty("format", out var formatEl) || formatEl.ValueKind != JsonValueKind.String)
        {
            return Fail("input.format is required (html | csv | xlsx | docx | pdf | json)");
        }
        if (!input.TryGetProperty("document", out var docEl) || docEl.ValueKind != JsonValueKind.Object)
        {
            return Fail("input.document is required and must be an object");
        }

        // Defense-in-depth: WorkflowExecutor is the authoritative gate for
        // unresolved templates, but if this handler is ever invoked from a
        // path that skipped resolution (manual testing, future REST endpoint,
        // replay tooling), scanning the document here catches the residue
        // before the PDF/CSV exporter happily writes `{{ ... }}` into output.
        // Cheap — the document is a bounded tree.
        var residuals = VariableResolver.FindUnresolvedTemplates(input);
        if (residuals.Count > 0)
        {
            var preview = residuals
                .Take(5)
                .Select(r => $"  - {r.Location}: {r.TemplateText}")
                .ToList();
            if (residuals.Count > preview.Count)
                preview.Add($"  … and {residuals.Count - preview.Count} more");
            _logger.LogError(
                "worker.report.unresolved_templates step_run_id={StepRunId} count={Count}",
                request.StepRunId, residuals.Count);
            return Fail(
                "report document contains unresolved `{{ ... }}` template references — fix the "
              + $"upstream template or step reference:\n{string.Join("\n", preview)}");
        }

        GenerateReportRequest req;
        try
        {
            req = new GenerateReportRequest
            {
                Format = formatEl.GetString() ?? string.Empty,
                Document = JsonSerializer.Deserialize<ReportDocument>(docEl.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ReportDocument(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "worker.report.failed step_run_id={StepRunId} reason={Reason}",
                request.StepRunId, "parse_document");
            return Fail($"could not parse document: {ex.Message}");
        }

        _logger.LogDebug(
            "worker.report.start step_run_id={StepRunId} workflow_run_id={WorkflowRunId} format={Format}",
            request.StepRunId, request.WorkflowRunId, req.Format);

        // Resolve the run's workflow + creator so the admin artifacts view can
        // filter artifacts by workflow AND attribute the document to the user
        // who triggered the run. Best-effort: a lookup failure must not block
        // the report.
        Guid? workflowId = null;
        Guid? userId = null;
        try
        {
            var origin = await _runs.GetRunOriginByRunIdAsync(request.WorkflowRunId, ct);
            workflowId = origin?.WorkflowId;
            // The run stores its creator as a string (WorkflowExecutor writes
            // the triggering user's Guid). Only attribute when it parses as a
            // real user id — legacy/system rows stay unattributed.
            if (Guid.TryParse(origin?.CreatedBy, out var creator) && creator != Guid.Empty)
            {
                userId = creator;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "worker.report.workflow_lookup_failed workflow_run_id={WorkflowRunId}", request.WorkflowRunId);
        }

        var context = new ReportGenerationContext
        {
            // Worker has no HTTP caller; attribution comes from the run's
            // CreatedBy (the user who triggered it), when present.
            UserId = userId,
            WorkflowRunId = request.WorkflowRunId,
            WorkflowId = workflowId,
            RequestId = null,
        };

        ReportArtifact artifact;
        try
        {
            artifact = await _reports.GenerateAndPersistAsync(req, source: "workflow", context, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Validation errors surface as InvalidOperationException from
            // ReportService (format unknown, title missing, too many
            // rows…). These are user-facing, not crash-worthy.
            _logger.LogError(
                ex,
                "worker.report.failed step_run_id={StepRunId} format={Format} reason={Reason}",
                request.StepRunId, req.Format, "generate");
            return Fail(ex.Message);
        }

        _logger.LogInformation(
            "worker.report.ok step_run_id={StepRunId} report_id={ReportId} format={Format} size_bytes={SizeBytes}",
            request.StepRunId, artifact.ReportArtifactId, artifact.Format, artifact.SizeBytes);

        // Decompress once — downstream nodes want the actual file bytes,
        // not the gzipped blob we persisted. Capping at the service's
        // 10 MB max keeps this bounded.
        var rawBytes = _reports.Decompress(artifact);
        var base64 = Convert.ToBase64String(rawBytes);

        var output = JsonSerializer.SerializeToElement(new
        {
            report_artifact_id = artifact.ReportArtifactId,
            filename = artifact.Filename,
            content_type = artifact.ContentType,
            format = artifact.Format,
            size_bytes = artifact.SizeBytes,
            sha256 = artifact.Sha256,
            download_url = $"/api/reports/{artifact.ReportArtifactId}/download",
            base64,
        });

        return new SnippetResult
        {
            Success = true,
            Output = output,
            // A report is not "just a file": the step wrote a ReportArtifact row and its
            // bytes, and both outlive the run. Generating one changed something.
            Change = StepChange.Changed,
            Logs = $"report.generated id={artifact.ReportArtifactId} format={artifact.Format} size={artifact.SizeBytes} stored={artifact.StoredBytes}",
        };
    }

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
