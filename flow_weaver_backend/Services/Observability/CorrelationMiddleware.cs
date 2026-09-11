using System.Security.Claims;
using Serilog.Context;

namespace flow_weaver_backend.Services.Observability;

// Enriches every request's Serilog scope with identifiers so downstream
// logs carry user + request context automatically. Nothing in the
// controllers or services has to remember to decorate messages.
//
// Fields added:
//   request_id   — UUIDv4 generated here. Echoed back in the
//                  X-Request-Id response header so a browser's devtools
//                  network panel can be joined with backend logs.
//   trace_id     — Honors an inbound W3C `traceparent` header (the
//                  trace-id segment) so calls traveling through a
//                  reverse proxy keep their distributed trace. Falls
//                  back to `request_id` when none is present.
//   user_id      — From `ClaimTypes.NameIdentifier`. Missing on
//                  anonymous routes (auth/login, bootstrap, health).
//   username     — From `ClaimTypes.Name` for easier log reading.
//   role         — First role claim; sufficient for our admin/operator/viewer model.
//   remote_ip    — The client address as resolved by ClientIp: the forwarded
//                  address when it came through a trusted proxy, otherwise
//                  the immediate peer. Never a raw untrusted header.
//   method/path  — HTTP verb + path for filter by route in log backends.
//
// Registered early in the pipeline so even 404s / 401s carry the fields.
public sealed class CorrelationMiddleware
{
    public const string RequestIdHeader = "X-Request-Id";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var requestId = ReadOrCreateRequestId(ctx);
        var traceId = ExtractTraceParent(ctx) ?? requestId;

        ctx.Response.Headers[RequestIdHeader] = requestId;

        var user = ctx.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = user?.FindFirstValue(ClaimTypes.Name);
        var role = user?.FindFirstValue(ClaimTypes.Role);

        using (LogContext.PushProperty("request_id", requestId))
        using (LogContext.PushProperty("trace_id", traceId))
        using (LogContext.PushProperty("user_id", userId))
        using (LogContext.PushProperty("username", username))
        using (LogContext.PushProperty("role", role))
        using (LogContext.PushProperty("remote_ip", ClientIp.Resolve(ctx)))
        using (LogContext.PushProperty("method", ctx.Request.Method))
        using (LogContext.PushProperty("path", ctx.Request.Path.Value))
        {
            await _next(ctx);
        }
    }

    // The correlation id for the request in flight, or null outside one.
    //
    // Single source of truth on purpose: AuditEvent.RequestId used to store
    // ctx.TraceIdentifier (ASP.NET's own connection-scoped id, shaped like
    // "0HN7…:00000001") while TraceEvent.RequestId and every Serilog line
    // carried the id below. Two different values meant an audit row could
    // never be joined to the logs or traces of the request that produced it —
    // which is the entire point of storing it.
    public static string? CurrentRequestId(HttpContext? ctx)
    {
        var value = ctx?.Response.Headers[RequestIdHeader].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Accepts an inbound `X-Request-Id` if the caller supplied one (useful
    // for manual debugging: `curl -H 'X-Request-Id: foo' …` and grep).
    // Otherwise mints a UUIDv4.
    private static string ReadOrCreateRequestId(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue(RequestIdHeader, out var existing))
        {
            var value = existing.ToString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128)
                return value;
        }
        return Guid.NewGuid().ToString();
    }

    // W3C traceparent format: "00-<trace_id 32 hex>-<span_id 16 hex>-<flags>".
    // We only care about the trace_id segment to correlate across hops.
    private static string? ExtractTraceParent(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("traceparent", out var value)) return null;
        var parts = value.ToString().Split('-');
        return parts.Length >= 2 && parts[1].Length == 32 ? parts[1] : null;
    }
}
