using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using flow_weaver_backend.Services.Observability;

namespace flow_weaver_backend.Services.Security;

// Centralized rate-limiter policy registration. Six buckets cover the
// expected traffic profiles of the API:
//
//   Policy          Partition   Limit          Motivation
//   ─────────────── ─────────── ────────────── ───────────────────────────
//   auth_login      per IP      5 / minute     Anti brute-force on /login
//                                              and /refresh. Complements
//                                              the per-user lockout added
//                                              in Sprint 1.3 — IP covers
//                                              attackers rotating usernames,
//                                              lockout covers attackers
//                                              rotating IPs.
//   auth_generic    per user    100 / minute   /logout, /change-password,
//                                              /me — generous so a normal
//                                              session never hits it.
//   read_heavy      per user    300 / minute   List + detail GETs of CRUDs.
//                                              Dashboards refresh freely.
//   write_normal    per user    60 / minute    Create/update/delete.
//   ai_chat         per user    30 / hour      Costly — every request
//                                              hits an external LLM.
//   workflow_run    per user    20 / hour      Very costly — schedules
//                                              actual work on devices.
//
// Every 429 response carries `Retry-After: 60` and a JSON body with
// `{ error: "rate_limited", retry_after_seconds: 60 }` so the frontend
// can back off cleanly.
public static class RateLimitingConfiguration
{
    public const string AuthLogin = "auth_login";
    public const string AuthGeneric = "auth_generic";
    public const string ReadHeavy = "read_heavy";
    public const string WriteNormal = "write_normal";
    public const string AiChat = "ai_chat";
    public const string WorkflowRun = "workflow_run";
    // Public webhook ingress (Git providers). Anonymous, partitioned by
    // (remote IP, target webhook id) so a misconfigured GitHub repo can't
    // starve other webhooks on the same instance.
    public const string GitWebhookIngest = "git_webhook_ingest";

    // Public messaging webhook ingress (Slack/Teams/WhatsApp/Telegram).
    // Anonymous, partitioned by (remote IP, channel id).
    public const string MessagingWebhookIngest = "messaging_webhook_ingest";

    // Public workflow-trigger webhook ingress. Anonymous, partitioned by
    // (remote IP, trigger id) so one noisy source can't starve other triggers.
    public const string WorkflowWebhookIngest = "workflow_webhook_ingest";

    public static IServiceCollection AddFlowWeaverRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "rate_limited", retry_after_seconds = 60 },
                    token);
            };

            // Per-IP: anti-brute-force on login/refresh. Runs BEFORE auth, so
            // we can't rely on a user claim — the client address is the best
            // partition key we have.
            //
            // It has to be the RESOLVED address (ClientIp), not the immediate
            // peer: with the frontend proxying /api, the raw peer is the same
            // container for everyone, so this budget of 5/minute was shared by
            // the whole platform — six people mistyping a password at once
            // locked each other out, while anyone reaching :8080 directly got
            // a private partition and wasn't limited at all.
            options.AddPolicy(AuthLogin, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientIp.ResolveOrUnknown(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(AuthGeneric, PerUser(100, TimeSpan.FromMinutes(1)));
            options.AddPolicy(ReadHeavy, PerUser(300, TimeSpan.FromMinutes(1)));
            options.AddPolicy(WriteNormal, PerUser(60, TimeSpan.FromMinutes(1)));
            options.AddPolicy(AiChat, PerUser(30, TimeSpan.FromHours(1)));
            options.AddPolicy(WorkflowRun, PerUser(20, TimeSpan.FromHours(1)));

            // Git webhook ingest: anonymous endpoint hit by external
            // providers. Partitioning by (IP, webhook id) gives backpressure
            // per source without coupling unrelated webhooks. 30/min/origin
            // matches GitHub's normal push cadence + retry headroom.
            options.AddPolicy(GitWebhookIngest, ctx =>
            {
                var ip = ClientIp.ResolveOrUnknown(ctx);
                var hookId = ctx.Request.RouteValues["id"]?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{ip}|{hookId}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
            });

            // Messaging webhook ingest: anonymous, hit by chat providers.
            // Partition by (IP, channel id). 120/min/origin gives headroom for
            // a busy group chat plus provider retries.
            options.AddPolicy(MessagingWebhookIngest, ctx =>
            {
                var ip = ClientIp.ResolveOrUnknown(ctx);
                var channelId = ctx.Request.RouteValues["channelId"]?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{ip}|{channelId}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
            });

            // Workflow-trigger webhook ingest: anonymous, partitioned by
            // (IP, trigger id). Each accepted hit fires a real workflow run, so
            // this is tighter than the Git/messaging surfaces — 20/min/origin
            // caps a burst (acceptance case (b)) while leaving room for a
            // legitimately chatty integration + retries.
            options.AddPolicy(WorkflowWebhookIngest, ctx =>
            {
                var ip = ClientIp.ResolveOrUnknown(ctx);
                var triggerId = ctx.Request.RouteValues["id"]?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{ip}|{triggerId}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }

    // Partitions by authenticated user id, falling back to the remote IP for
    // anonymous requests. Prevents a single attacker from spinning up many
    // sessions to bypass per-user quotas.
    private static Func<HttpContext, RateLimitPartition<string>> PerUser(int limit, TimeSpan window) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? ClientIp.Resolve(ctx)
                ?? "anon",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = window,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            });
}
