namespace flow_weaver_backend.Services.Security;

// Adds a fixed set of security response headers to every HTTP response.
// Must be registered BEFORE UseCors so these headers reach the browser on
// preflight (OPTIONS) responses too.
//
// CSP notes:
//   - Scalar UI (/scalar/v1) bootstraps from cdn.jsdelivr.net, so that
//     origin is allowlisted for scripts and styles.
//   - 'unsafe-inline' for styles is required by Scalar's injected theme
//     tags. Trade-off accepted: the attack surface of an inline-style
//     XSS on an admin docs UI is acceptable for MVP; revisit when the
//     frontend lives on the same origin and Scalar moves to hashed CSP.
//   - SSE endpoints (/api/ai/v2/chat/stream) set `X-Accel-Buffering: no`
//     themselves; this middleware does not touch streaming concerns.
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // HSTS — tell browsers to always use HTTPS for the next year.
        // Safe to emit even on HTTP in dev; browsers only honor it when
        // the response was served over HTTPS.
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        // No MIME-type sniffing.
        headers["X-Content-Type-Options"] = "nosniff";

        // No framing — blocks clickjacking.
        headers["X-Frame-Options"] = "DENY";

        // Don't leak the full URL as Referer to cross-origin navigations.
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Disable browser features we never use.
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        // CSP — tight default, permissive only where Scalar needs it.
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
            "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
            "img-src 'self' data: https://cdn.jsdelivr.net; " +
            "connect-src 'self'; " +
            "font-src 'self' data: https://cdn.jsdelivr.net; " +
            "frame-ancestors 'none'";

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
