using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using flow_weaver_backend.Services.Security;

namespace flow_weaver_backend.Services.Ai.Conversation;

// One tool call a reply really made, as kept with that reply in the conversation.
// Short on purpose: it is what the next turn's model reads to know what was done,
// not a copy of the result.
public sealed record StoredToolCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("args")] string Args,
    [property: JsonPropertyName("result")] string Result);

// The conversation history only kept each reply's text, so a reply that worked
// through tools without writing anything (or that wrote "done" without calling
// one) left the next turn nothing to go on: the model started the same
// exploration from scratch, or trusted a claim no tool backed. Each reply now
// carries the list of calls it made, and the replay puts a server-written record
// of it in front of the user message that followed.
public static partial class ToolRunRecord
{
    public const int MaxArgsChars = 120;
    public const int MaxResultChars = 160;
    public const int MaxRenderChars = 1_500;
    public const string RedactedMarker = "[REDACTED]";

    private static readonly SecretRedactor Redactor = new();

    [GeneratedRegex(@"(?i)(pass(word|wd)?|secret|token|api[_-]?key|credential|authorization|private[_-]?key)")]
    private static partial Regex SecretKey();

    public static StoredToolCall Entry(string name, JsonElement args, bool ok, string resultRaw) =>
        new(name, ok, Clip(Redactor.Redact(RedactArgs(args)).redacted, MaxArgsChars),
            Clip(Redactor.Redact(resultRaw).redacted, MaxResultChars));

    // What the model reads in front of the next user message.
    public static string Render(IReadOnlyList<StoredToolCall> calls)
    {
        if (calls.Count == 0)
            return "[Flow Weaver server record of your previous reply: it ran no tools, so nothing was read, "
                + "created or changed in it.]";

        var failed = calls.Count(c => !c.Ok);
        var sb = new StringBuilder()
            .Append("[Flow Weaver server record of your previous reply — tools actually run: ")
            .Append(calls.Count).Append(" (").Append(calls.Count - failed).Append(" ok, ")
            .Append(failed).Append(" failed)");

        var listed = 0;
        foreach (var c in calls)
        {
            var line = $"\n- {c.Name} {(c.Ok ? "ok" : "FAILED")} {c.Args} → {c.Result}";
            if (sb.Length + line.Length > MaxRenderChars) break;
            sb.Append(line);
            listed++;
        }
        if (listed < calls.Count)
            sb.Append("\n…and ").Append(calls.Count - listed).Append(" more calls not listed.");
        return sb.Append(" Do not repeat calls whose result you already have unless something changed.]").ToString();
    }

    // Reads the `tools` array of a stored assistant message. Null when the message
    // was written before the record existed: it gets no record rather than a false
    // "ran no tools".
    public static List<StoredToolCall>? Read(JsonElement message)
    {
        if (!message.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return null;
        try
        {
            return tools.Deserialize<List<StoredToolCall>>() ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RedactArgs(JsonElement args)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "{}";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            WriteRedacted(w, args);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteRedacted(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    if (SecretKey().IsMatch(p.Name) && p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        w.WriteStringValue(RedactedMarker);
                    else
                        WriteRedacted(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray()) WriteRedacted(w, item);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
