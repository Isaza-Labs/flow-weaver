using System.Text.Json;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Import;

// Produces a minimal Snippet row for a referenced-but-missing snippet
// when the user chooses "create stub" in the wizard. The stub is
// editable in /snippets/{id} and is wired to the imported workflow so
// runs proceed (returning placeholder output) instead of erroring out.
//
// `idempotency` defaults to null (= inherit handler floor). The
// `verified=false` flag tells the editor's policy gate that this is a
// freshly-stamped snippet that should not move to production without
// a human review.
public sealed class SnippetStubBuilder
{
    public SnippetModel Build(string idInImport, string inferredType, string? authorEmail)
    {
        var now = DateTime.UtcNow;
        var name = SanitiseName(idInImport);

        return new SnippetModel
        {
            SnippetId = Guid.NewGuid(),
            Name = name.Length > 0 ? name : $"imported_{Guid.NewGuid().ToString("N")[..8]}",
            Type = inferredType,
            Description = $"Auto-generated stub from import. Original reference: '{idInImport}'. Replace the body before promoting to production.",
            InputSchema = ParseOrEmpty("{}"),
            OutputSchema = ParseOrEmpty("{}"),
            Code = DefaultBodyFor(inferredType),
            ScriptLanguage = inferredType == "python_snippet" ? "python" : null,
            TargetMode = "once",
            MaxParallel = 1,
            TimeoutSeconds = 60,
            Verified = false,
            RetryPolicy = ParseOrEmpty("{}"),
            CreatedBy = authorEmail,
            LogicDiagramMermaid = null,
            Idempotency = null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static string SanitiseName(string raw)
    {
        var cleaned = new string(raw
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ? c : '_')
            .ToArray())
            .Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static JsonElement ParseOrEmpty(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static string DefaultBodyFor(string type) => type switch
    {
        "python_snippet" => """
            # Auto-generated stub. Replace with your logic.
            from flowweaver_runtime import get_input, set_output

            payload = get_input()
            set_output({"placeholder": True, "received": payload})
            """,
        "transform" => """
            // jmespath expression — replace with your transform.
            @
            """,
        "rest_call" => """
            {
              "method": "GET",
              "url": "",
              "headers": {},
              "body": null
            }
            """,
        _ => "",
    };
}
