using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Utils.Report;
using Microsoft.AspNetCore.Http;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Tool surface for the LLM. Accepts the same shape as POST /api/reports
// plus picks up the agent conversation context transparently via
// IToolExecutionContext so we never ask the model to re-supply things it
// already knows.
//
// Return shape is deliberately small for the tool_result preview (the
// agent sees filename + content_type + size + base64) but the caller
// (chat UI, workflows) can follow up with GET /api/admin/reports/{id}
// for the full metadata + re-download.
public sealed class GenerateReportHandler : IToolHandler
{
    public string Name => "generate_report";

    public string Description =>
        "Tier: single_confirm. Generate ONE downloadable report format from a "
        + "structured document: html, csv, json (the document as data, for feeding another system), "
        + "xlsx (a spreadsheet to work in), docx (a Word document the reader will keep editing) or "
        + "pdf (final, for sending or printing). Pick by what the user will DO with it. "
        + "Use this when the user asks for an exportable artifact — inventory diffs, "
        + "run summaries, audit digests. Returns report_artifact_id + filename + content_type + base64. The "
        + "report is also persisted in /admin/reports with full audit trail. Do NOT put secrets (passwords, "
        + "API keys, tokens) in the document; use references instead. "
        + "To EMAIL the file, do NOT paste the returned base64 into the attachment — put "
        + "'${report:<report_artifact_id>}' in the email attachment's content_base64 and the backend "
        + "splices in the bytes server-side. For SEVERAL formats in one email, call generate_report once "
        + "per format and attach each report_artifact_id as its own attachments[] entry in a single "
        + "send_with_attachment call. Include these calls inside the single batch Plan block; do not emit "
        + "a fresh confirmation between them and the email/upload step.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["format", "document"],
      "properties": {
        "format": {
          "type": "string",
          "enum": ["html", "csv", "xlsx", "docx", "pdf", "json"],
          "description": "html for inline view, pdf for print, xlsx when the user wants to filter/sort columns, csv for pipeline consumption."
        },
        "document": {
          "type": "object",
          "additionalProperties": false,
          "required": ["title"],
          "properties": {
            "title":        { "type": "string", "minLength": 1, "maxLength": 200 },
            "subtitle":     { "type": "string" },
            "badge":        { "type": "string" },
            "generated_by": { "type": "string" },
            "stats": {
              "type": "array",
              "maxItems": 12,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["label", "value"],
                "properties": {
                  "label": { "type": "string" },
                  "value": { "type": "string" },
                  "hint":  { "type": "string" },
                  "tone":  { "type": "string", "enum": ["critical","high","medium","low","ok","accent","neutral"] }
                }
              }
            },
            "sections": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title"],
                "properties": {
                  "title":       { "type": "string" },
                  "description": { "type": "string" },
                  "category":    { "type": "string", "enum": ["critical","api","ai","engine","store","worker","scheduler","frontend"] },
                  "tables": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "caption": { "type": "string" },
                        "headers": { "type": "array", "items": { "type": "string" } },
                        "rows": {
                          "type": "array",
                          "items": { "type": "array", "items": { "type": "string" } }
                        },
                        "severity_columns": {
                          "type": "object",
                          "additionalProperties": { "type": "string", "enum": ["critical","high","medium","low","ok"] }
                        }
                      }
                    }
                  },
                  "callouts": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["title","body"],
                      "properties": {
                        "title": { "type": "string" },
                        "body":  { "type": "string" },
                        "tone":  { "type": "string", "enum": ["info","warn","danger","success"] }
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
    """).RootElement;

    private readonly IReportService _reports;
    private readonly IToolExecutionContext _ctx;
    private readonly ICurrentUser _caller;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<GenerateReportHandler> _logger;

    public GenerateReportHandler(
        IReportService reports,
        IToolExecutionContext ctx,
        ICurrentUser caller,
        IHttpContextAccessor http,
        ILogger<GenerateReportHandler> logger)
    {
        _reports = reports;
        _ctx = ctx;
        _caller = caller;
        _http = http;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // Round-trip through JSON → DTO so the schema validation on the
        // OpenAI side is the contract and we don't repeat it manually.
        GenerateReportRequest request;
        try
        {
            request = JsonSerializer.Deserialize<GenerateReportRequest>(args.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("could not parse generate_report arguments");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.tool.generate_report.validation_failed reason=invalid_arguments");
            throw;
        }

        _logger.LogDebug(
            "ai.tool.generate_report.start format={Format} title_chars={TitleChars}",
            request.Format, request.Document.Title.Length);

        try
        {
            var context = new ReportGenerationContext
            {
                UserId = _caller.IsAuthenticated ? _caller.UserId : null,
                AgentConversationId = _ctx.ConversationId,
                AgentRunId = _ctx.AgentRunId,
                AgentName = _ctx.AgentName,
                AgentPrompt = _ctx.UserMessage,
                RequestId = _http.HttpContext?.TraceIdentifier,
            };

            var artifact = await _reports.GenerateAndPersistAsync(request, "agent", context, ct);

            // Decompress here because the tool return IS the downloadable
            // payload; the LLM shouldn't have to understand gzip. Cap what
            // the agent sees in `preview` to avoid blowing the context — the
            // full bytes still go to the `base64` field for the caller.
            var rawBytes = _reports.Decompress(artifact);
            var result = new
            {
                report_artifact_id = artifact.ReportArtifactId,
                filename = artifact.Filename,
                content_type = artifact.ContentType,
                format = artifact.Format,
                size_bytes = artifact.SizeBytes,
                sha256 = artifact.Sha256,
                expires_at = artifact.ExpiresAt,
                base64 = Convert.ToBase64String(rawBytes),
                preview_available_at = $"/api/admin/reports/{artifact.ReportArtifactId}",
            };

            _logger.LogInformation(
                "ai.tool.generate_report.ok report_artifact_id={ReportArtifactId} format={Format} size_bytes={SizeBytes}",
                artifact.ReportArtifactId, artifact.Format, artifact.SizeBytes);

            return JsonSerializer.SerializeToElement(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.generate_report.failed format={Format}", request.Format);
            throw;
        }
    }
}
