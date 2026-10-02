using System.Text.Json;
using DevLab.JmesPath;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Applies a JMESPath expression to an input JSON payload and returns the
// result. Input: `{ "language": "jmespath", "expression": "items[?active]",
// "input": { ... } }`. If `input` is omitted the entire InputPayload is
// the document root.
public sealed class TransformHandler : ISnippetHandler
{
    public string Type => "transform";

    private readonly ILogger<TransformHandler> _logger;

    public TransformHandler(ILogger<TransformHandler> logger)
    {
        _logger = logger;
    }

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // Older bundles spell a transform's projection `mapping`; it is the same
        // JMESPath multiselect hash this engine reads from `expression`
        // (snippets/SPEC.md `transform`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);

        // Language preference: snippet's ScriptLanguage wins,
        // then inline input.language, then default "jmespath".
        var language = !string.IsNullOrWhiteSpace(request.ScriptLanguage)
            ? request.ScriptLanguage!
            : input.ValueKind == JsonValueKind.Object
              && input.TryGetProperty("language", out var l)
                ? l.GetString() ?? "jmespath"
                : "jmespath";

        if (!string.Equals(language, "jmespath", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "worker.transform.failed step_run_id={StepRunId} reason={Reason} language={Language}",
                request.StepRunId, "unsupported_language", language);
            return Task.FromResult<SnippetResult>(new()
            {
                // Pure: a JMESPath expression over a document, no I/O in this handler.
                Change = StepChange.Unchanged,
                Success = false,
                Error = $"unsupported transform language '{language}' (only jmespath is supported)",
            });
        }

        // Expression lives on the snippet row (SnippetCode); only fall back
        // to an inline `input.expression` when the caller is running an
        // ad-hoc transform without a snippet.
        string expression;
        if (!string.IsNullOrWhiteSpace(request.SnippetCode))
            expression = request.SnippetCode!;
        else if (input.ValueKind == JsonValueKind.Object
                 && input.TryGetProperty("expression", out var e))
            expression = e.GetString() ?? "*";
        else
            expression = "*";

        // The document to transform: explicit `input` property or the
        // entire payload (minus language/expression keys).
        string document;
        if (input.TryGetProperty("input", out var docEl))
        {
            document = docEl.GetRawText();
        }
        else
        {
            document = input.GetRawText();
        }

        _logger.LogDebug(
            "worker.transform.start step_run_id={StepRunId} language={Language} input_bytes={InputBytes}",
            request.StepRunId, language, document?.Length ?? 0);

        try
        {
            var jmes = new JmesPath();
            var result = jmes.Transform(document, expression);

            var outputEl = JsonDocument.Parse(result).RootElement;

            _logger.LogInformation(
                "worker.transform.ok step_run_id={StepRunId} language={Language} output_bytes={OutputBytes}",
                request.StepRunId, language, result?.Length ?? 0);

            return Task.FromResult(new SnippetResult
            {
                // Pure: a JMESPath expression over a document, no I/O in this handler.
                Change = StepChange.Unchanged,
                Success = true,
                Output = outputEl,
                Logs = $"jmespath: {expression}",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "worker.transform.failed step_run_id={StepRunId} language={Language} reason={Reason}",
                request.StepRunId, language, "jmespath_error");
            return Task.FromResult<SnippetResult>(new()
            {
                // Pure: a JMESPath expression over a document, no I/O in this handler.
                Change = StepChange.Unchanged,
                Success = false,
                Error = $"jmespath transform failed: {ex.Message}",
                Logs = ex.ToString(),
            });
        }
    }
}
