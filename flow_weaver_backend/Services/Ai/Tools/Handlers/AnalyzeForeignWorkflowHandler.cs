using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// FU-3: chat-tool front for the import pipeline.
//
// The wizard already exposes the analyzer via REST + SSE (under
// /api/workflow/import). This handler lets a user in /ai/chat paste a
// workflow definition and ask "translate this to v1" without leaving
// the conversation. The analysis result is returned as JSON the agent
// then summarises in natural language.
//
// We run the same `WorkflowImportPipeline` the wizard uses against a
// temporary in-memory draft; once analysis completes we delete the
// draft (chat does not persist, by design).
public sealed class AnalyzeForeignWorkflowHandler : IToolHandler
{
    // Tighter cap than the wizard's 5 MiB — chat token budgets are
    // measured in tens of thousands of tokens and a huge paste would
    // blow past the LLM's context window before the response renders.
    private const int MaxRawBytes = 16 * 1024;

    public string Name => "analyze_foreign_workflow";

    public string Description =>
        "Detect the format of a foreign workflow definition (FlowWeaver v1, n8n, Itential, generic DAG) " +
        "and translate it to FlowWeaver's v1 schema. Returns the proposed workflow plus the list of " +
        "missing snippets / integrations / vendor commands so the agent can guide the user through " +
        "creating them. Does NOT persist anything — for full import use /workflows/import.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["raw_text"],
          "properties": {
            "raw_text":    { "type": "string", "description": "YAML or JSON workflow body, up to 16 KiB" },
            "format_hint": { "type": "string", "description": "Optional: flow_weaver_v1 | n8n | itential | generic_dag" }
          }
        }
        """).RootElement.Clone();

    private readonly WorkflowImportPipeline _pipeline;
    private readonly ImportDraftCache _cache;
    private readonly ICurrentUser _caller;
    private readonly ILogger<AnalyzeForeignWorkflowHandler> _logger;

    public AnalyzeForeignWorkflowHandler(
        WorkflowImportPipeline pipeline,
        ImportDraftCache cache,
        ICurrentUser caller,
        ILogger<AnalyzeForeignWorkflowHandler> logger)
    {
        _pipeline = pipeline;
        _cache = cache;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("raw_text", out var rt) || rt.ValueKind != JsonValueKind.String)
            return ToJson(new { error = "raw_text is required" });

        var rawText = rt.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(rawText))
            return ToJson(new { error = "raw_text is empty" });

        var byteCount = Encoding.UTF8.GetByteCount(rawText);
        if (byteCount > MaxRawBytes)
        {
            return ToJson(new
            {
                error = $"raw_text exceeds the chat tool's {MaxRawBytes / 1024} KiB limit. " +
                        "Use POST /workflow/import/analyze for larger payloads.",
            });
        }

        var hint = args.TryGetProperty("format_hint", out var fh) && fh.ValueKind == JsonValueKind.String
            ? fh.GetString() ?? ""
            : "";

        var draft = _cache.Create(_caller.UserId, hint,
            Encoding.UTF8.GetBytes(rawText));

        try
        {
            // Run synchronously. We awaited the tool call from the chat
            // loop, so we can block here — the wizard's async path is
            // motivated by SSE-driven progress, which chat doesn't need.
            await _pipeline.RunAsync(draft, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ai.tool.analyze_foreign_workflow.pipeline_failed");
            _cache.Delete(draft.Token);
            return ToJson(new { error = $"pipeline error: {ex.Message}" });
        }

        if (draft.Status == ImportDraftStatus.Failed)
        {
            var error = draft.Error ?? "unknown";
            _cache.Delete(draft.Token);
            return ToJson(new { error });
        }

        var report = draft.Report;
        _cache.Delete(draft.Token);

        if (report is null)
            return ToJson(new { error = "no report produced" });

        // Slim payload — we leave out the full proposed_workflow body to
        // keep the chat token budget tight. The agent can ask for it via
        // a follow-up tool call if needed, or the user can move to the
        // wizard for the full review.
        return ToJson(new
        {
            format_detected = report.FormatDetected,
            confidence = report.Confidence,
            translation_notes = report.TranslationNotes,
            missing_snippets = report.MissingDependencies.Snippets
                .Select(s => new { id = s.IdInImport, inferred_type = s.InferredType })
                .ToArray(),
            missing_integrations = report.MissingDependencies.Integrations
                .Select(i => new { id = i.IdInImport, inferred_base_url = i.InferredBaseUrl })
                .ToArray(),
            warnings = report.Warnings,
            node_count = CountArray(report.ProposedWorkflow, "nodes"),
            edge_count = CountArray(report.ProposedWorkflow, "edges"),
            hint_for_user = "Open /workflows/import and re-upload the same file to commit it. The wizard " +
                "lets you resolve missing dependencies and pick a conflict-resolution strategy.",
        });
    }

    private static int CountArray(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        if (!obj.TryGetProperty(key, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
    }

    private static JsonElement ToJson(object payload)
        => JsonSerializer.SerializeToElement(payload);
}
