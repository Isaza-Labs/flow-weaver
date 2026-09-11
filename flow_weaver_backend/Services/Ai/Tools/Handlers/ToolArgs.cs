using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Small typed accessors over a tool call's JSON args object. Every getter is
// null-safe: a missing key or wrong JSON kind returns null rather than throwing.
internal static class ToolArgs
{
    public static string? Str(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    public static bool? Bool(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    public static Guid? GuidVal(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g
            : null;

    public static JsonElement? Obj(JsonElement a, string k)
        => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? v.Clone() : null;
}
