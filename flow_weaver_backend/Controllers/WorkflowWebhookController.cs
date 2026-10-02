using flow_weaver_backend.Services.Observability;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Public ingestion endpoint for `webhook`-type WorkflowTriggers. PUBLIC — the caller (a third-party system, a CI job, curl) can't
// authenticate with a JWT, so auth is the per-trigger HMAC secret validated by
// the receiver. Distinct route from the JWT-authed trigger CRUD; the path here
// is what users paste into their webhook sender.
//
// The three acceptance cases all land in /admin/audit:
//   (a) legit signed hit           → 202, audit status "ok", run enqueued.
//   (b) burst                      → 429 by the rate limiter (per IP+trigger);
//                                     accepted hits still audited.
//   (c) payload with an internal URL → the ingest makes NO outbound call, so
//                                     it's inert; if the fired workflow uses the
//                                     URL, IUrlGuard blocks it at execution.
[ApiController]
[Route("api/webhooks/workflow")]
[AllowAnonymous]
public class WorkflowWebhookController : ControllerBase
{
    private readonly WorkflowWebhookReceiver _receiver;
    private readonly AppDbContext _db;
    private readonly ILogger<WorkflowWebhookController> _logger;

    public WorkflowWebhookController(
        WorkflowWebhookReceiver receiver,
        AppDbContext db,
        ILogger<WorkflowWebhookController> logger)
    {
        _receiver = receiver;
        _db = db;
        _logger = logger;
    }

    [HttpPost("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WorkflowWebhookIngest)]
    public async Task<IActionResult> Ingest(Guid id, CancellationToken ct)
    {
        // Buffer the body — needed both for HMAC and to pass to the receiver.
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var body = ms.ToArray();
        if (body.Length > 1024 * 1024)
            return Problems.PayloadTooLarge("payload too large (>1 MiB)", code: "webhook_too_large");

        var signature = Request.Headers["X-FlowWeaver-Signature"].FirstOrDefault()
                        ?? Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        var token = Request.Headers["X-FlowWeaver-Token"].FirstOrDefault();

        var outcome = await _receiver.ReceiveAsync(id, signature, token, body, ct);
        _logger.LogInformation(
            "workflow_webhook.ingest trigger_id={Id} status={Status} message={Message}",
            id, outcome.StatusCode, outcome.Message);

        await WriteIngestAuditAsync(id, signature is not null || token is not null, body.Length, outcome, ct);

        return StatusCode(outcome.StatusCode, new
        {
            ok = outcome.StatusCode is >= 200 and < 300,
            message = outcome.Message,
            workflow_run_id = outcome.WorkflowRunId,
        });
    }

    // Append-only audit per ingest hit so security can correlate webhook
    // activity in /admin/audit. The workflow is resolved from the trigger row when
    // known; unknown-trigger hits land under Guid.Empty so they're still
    // searchable. Mirrors GitWebhookIngestController.WriteIngestAuditAsync. 429
    // is short-circuited by the rate limiter before reaching here, so there's no
    // rate_limited mapping.
    private async Task WriteIngestAuditAsync(
        Guid triggerId, bool signed, int bodyBytes,
        WorkflowWebhookReceiver.Outcome outcome, CancellationToken ct)
    {
        var trigger = await _db.WorkflowTriggers
            .AsNoTracking()
            .Where(t => t.WorkflowTriggerId == triggerId)
            .Select(t => new { t.WorkflowId })
            .FirstOrDefaultAsync(ct);

        var status = outcome.StatusCode switch
        {
            >= 200 and < 300 => "ok",
            401 or 403 => "rejected",
            404 => "unknown",
            413 => "too_large",
            503 => "backpressure",
            _ => "error",
        };

        var payload = new
        {
            workflow_id = trigger?.WorkflowId,
            signed,
            body_bytes = bodyBytes,
            status_code = outcome.StatusCode,
            workflow_run_id = outcome.WorkflowRunId,
            message = outcome.Message,
        };

        try
        {
            _db.AuditLogs.Add(new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                UserId = null,
                EntityType = "WorkflowTrigger",
                EntityId = triggerId,
                Action = $"workflow_webhook.ingest.{status}",
                BeforeJson = JsonDocument.Parse("null").RootElement,
                AfterJson = JsonSerializer.SerializeToElement(payload),
                Actor = "workflow-webhook",
                Ip = ClientIp.Resolve(HttpContext),
                UserAgent = HttpContext.Request.Headers.UserAgent.ToString(),
                At = DateTime.UtcNow,
                RequestId = CorrelationMiddleware.CurrentRequestId(HttpContext),
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "workflow_webhook.audit_failed trigger_id={Id} status={Status}", triggerId, status);
        }
    }
}
