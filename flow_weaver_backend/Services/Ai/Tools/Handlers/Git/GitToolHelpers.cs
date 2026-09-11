using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

internal static class GitToolHelpers
{
    // Unwrap an ActionResult<T> coming from IGitService into either a
    // success (T) or a structured error JSON the agent can read. The
    // service uses ObjectResult with StatusCode for failures, and the
    // implicit T → ActionResult<T> conversion for the happy path.
    public static (bool ok, T? value, JsonElement error) Unwrap<T>(ActionResult<T> ar)
    {
        if (ar.Result is ObjectResult or)
        {
            var status = or.StatusCode ?? 200;
            if (status is >= 200 and < 300 && or.Value is T good)
                return (true, good, default);
            return (false, default, JsonSerializer.SerializeToElement(new
            {
                error = or.Value,
                status,
            }));
        }
        if (ar.Value is { } v) return (true, v, default);
        return (false, default, JsonSerializer.SerializeToElement(new { error = "unknown error" }));
    }

    public static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool GetBool(JsonElement args, string key, bool fallback = false) =>
        args.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    public static Guid? GetGuid(JsonElement args, string key)
    {
        var s = GetString(args, key);
        return Guid.TryParse(s, out var g) ? g : null;
    }
}
