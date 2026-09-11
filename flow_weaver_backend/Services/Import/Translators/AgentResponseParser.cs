using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Translators;

// Pure-function helper that parses the LLM's response into a
// TranslationResult. Lives outside AgentTranslator so the parsing
// rules (markdown-fence stripping, wrapped-vs-bare shape, malformed
// JSON fallback) are testable as a standalone unit without standing up
// a mock LLM stack.
public static class AgentResponseParser
{
    // Accepts either the wrapped shape
    //   { "workflow": {...}, "notes": [...], "warnings": [...] }
    // or a bare v1 workflow object. Returns a fallback empty v1 when
    // the response is not valid JSON.
    public static TranslationResult Parse(string raw)
    {
        var json = ExtractJsonBlock(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("workflow", out var wf))
            {
                return new TranslationResult
                {
                    V1Workflow = wf.Clone(),
                    Notes = ReadStringArray(root, "notes"),
                    Warnings = ReadStringArray(root, "warnings"),
                };
            }

            return new TranslationResult
            {
                V1Workflow = root,
                Notes = new[] { "Agent returned a bare workflow object; no commentary." },
                Warnings = Array.Empty<string>(),
            };
        }
        catch (JsonException ex)
        {
            return new TranslationResult
            {
                V1Workflow = EmptyV1(),
                Notes = new[] { "Agent returned non-JSON; translation skipped." },
                Warnings = new[] { $"Parse error: {ex.Message}" },
            };
        }
    }

    // Strips Markdown code fences (```json / ``` ... ```). Some LLMs
    // wrap their JSON despite the system prompt asking for plain JSON.
    public static string ExtractJsonBlock(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }

    public static JsonElement EmptyV1()
    {
        return JsonDocument.Parse("""
            {
              "schema_version": "v1",
              "name": "Imported workflow (translation failed)",
              "description": null,
              "input_schema": {},
              "nodes": [
                { "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0, "type": "sentinel", "config_overrides": {} },
                { "id": "__end__", "snippet_id": "__end__", "x": 400, "y": 0, "type": "sentinel", "config_overrides": {} }
              ],
              "edges": [
                { "source": "__start__", "target": "__end__", "type": "success" }
              ],
              "metadata": {}
            }
            """).RootElement.Clone();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();
    }
}
