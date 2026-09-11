using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Shared translation of a service's `ActionResult<T>` into a normalized
// outcome the config tool handlers can turn into a JSON tool result. All the
// domain services (ISnippet, IPolicy, IVendorCommand, …) return the payload
// either directly (implicit conversion → `.Value`) or wrapped in an
// ObjectResult subclass (CreatedAtActionResult/OkObjectResult for success,
// BadRequest/NotFound/Conflict for failure). This collapses both into
// (Value, Status, Error).
internal readonly record struct ServiceOutcome<T>(T? Value, int Status, string? Error)
    where T : class
{
    public bool Ok => Value is not null && Status is >= 200 and < 300;
}

internal static class ToolResults
{
    public static ServiceOutcome<T> Read<T>(ActionResult<T> result) where T : class
    {
        // ObjectResult covers CreatedAtActionResult / OkObjectResult (success)
        // and BadRequest/NotFound/Conflict (failure) — all subclasses.
        if (result.Result is ObjectResult obj)
        {
            var status = obj.StatusCode ?? 200;
            if (status is >= 200 and < 300 && obj.Value is T ok)
                return new(ok, status, null);
            return new(null, status is >= 200 and < 300 ? 400 : status, ExtractError(obj.Value));
        }

        // Payload returned directly via implicit conversion to ActionResult<T>.
        if (result.Value is T v)
            return new(v, 200, null);

        return new(null, 500, "unexpected service result");
    }

    // Services return an anonymous `{ error = "..." }`; round-trip through JSON
    // so we don't depend on the anonymous type, and fall back to the raw body
    // (e.g. a ProblemDetails) so the agent always gets *something* legible.
    public static string? ExtractError(object? value)
    {
        if (value is null) return null;
        try
        {
            var el = JsonSerializer.SerializeToElement(value);
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
            return el.GetRawText();
        }
        catch
        {
            return value.ToString();
        }
    }

    // Standard hint appended to a permission-denied tool result so the agent
    // relays *why* and doesn't retry.
    public static string? PermissionHint(int status, string requirement) =>
        status == 403 ? $"This requires the {requirement} role; a lower-privileged user cannot do it." : null;
}
