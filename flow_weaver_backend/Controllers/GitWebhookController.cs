using flow_weaver_backend.Services.Observability;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Two surfaces in one file:
//
//  1. CRUD endpoints (under /api/git/repositories/{repoId}/webhooks)
//     authed like the rest of the Git API. Admin to mutate, Viewer to
//     read.
//
//  2. The receiver endpoint (POST /api/git/webhooks/{id}) which is
//     PUBLIC — GitHub/GitLab can't authenticate. It validates the HMAC
//     signature against the secret stored on the row and runs the
//     dispatch pipeline.
//
// Both live here because the two surfaces share types and never
// diverge in practice; splitting buys nothing but a second [Route].
[ApiController]
[Route("api/git/repositories/{repoId:guid}/webhooks")]
[HasPermission("gitwebhook.read")]
public class GitWebhookCrudController : ControllerBase
{
    private readonly IGitWebhookService _service;

    public GitWebhookCrudController(IGitWebhookService service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<GitWebhookResponse>>> List(
        Guid repoId, CancellationToken ct)
        => _service.ListAsync(repoId, ct);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<GitWebhookResponse>> Get(
        Guid repoId, Guid id, CancellationToken ct)
        => _service.GetAsync(repoId, id, ct);

    [HttpPost]
    [HasPermission("gitwebhook.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitWebhookResponse>> Create(
        Guid repoId, [FromBody] CreateGitWebhook dto, CancellationToken ct)
        => _service.CreateAsync(repoId, dto, ct);

    [HttpPut("{id:guid}")]
    [HasPermission("gitwebhook.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitWebhookResponse>> Update(
        Guid repoId, Guid id, [FromBody] UpdateGitWebhook dto, CancellationToken ct)
        => _service.UpdateAsync(repoId, id, dto, ct);

    [HttpDelete("{id:guid}")]
    [HasPermission("gitwebhook.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<GitWebhookResponse>> Delete(
        Guid repoId, Guid id, CancellationToken ct)
        => _service.DeleteAsync(repoId, id, ct);

    [HttpGet("{id:guid}/deliveries")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<GitWebhookDeliveryResponse>>> Deliveries(
        Guid repoId, Guid id,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => _service.ListDeliveriesAsync(repoId, id, limit, ct);
}

// Public ingestion endpoint. Note the `[AllowAnonymous]` and the
// distinct route — the path here is what users paste into GitHub /
// GitLab as their webhook target.
[ApiController]
[Route("api/git/webhooks")]
[AllowAnonymous]
public class GitWebhookIngestController : ControllerBase
{
    private readonly GitWebhookReceiver _receiver;
    private readonly AppDbContext _db;
    private readonly ILogger<GitWebhookIngestController> _logger;

    public GitWebhookIngestController(
        GitWebhookReceiver receiver,
        AppDbContext db,
        ILogger<GitWebhookIngestController> logger)
    {
        _receiver = receiver;
        _db = db;
        _logger = logger;
    }

    [HttpPost("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.GitWebhookIngest)]
    public async Task<IActionResult> Ingest(Guid id, CancellationToken ct)
    {
        // Buffer the body — we need it both for HMAC and for parse.
        // Cap at 1 MiB; GitHub's largest practical push payload is well
        // under this even for 100-commit batches.
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var body = ms.ToArray();
        if (body.Length > 1024 * 1024)
            return Problems.PayloadTooLarge("payload too large (>1 MiB)", code: "webhook_too_large");

        // Provider-specific event header. Try them in order — the
        // receiver re-parses the body for fields that aren't always
        // header-encoded.
        var evt = Request.Headers["X-GitHub-Event"].FirstOrDefault()
                  ?? Request.Headers["X-Gitlab-Event"].FirstOrDefault()
                  ?? Request.Headers["X-FlowWeaver-Event"].FirstOrDefault();

        var sig = Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
                  ?? Request.Headers["X-Gitlab-Token"].FirstOrDefault()
                  ?? Request.Headers["X-FlowWeaver-Signature"].FirstOrDefault();

        var outcome = await _receiver.ReceiveAsync(id, evt, sig, body, ct);
        _logger.LogInformation(
            "git.webhook.ingest webhook_id={Id} status={Status} message={Message}",
            id, outcome.StatusCode, outcome.Message);

        // Append-only audit per ingest hit. The receiver writes a delivery
        // row already (per-webhook history); this row joins the global
        // /admin/audit feed so security can correlate webhook activity
        // with other events.
        await WriteIngestAuditAsync(id, evt, sig is not null, body.Length, outcome, ct);

        return StatusCode(outcome.StatusCode, new
        {
            ok = outcome.StatusCode is >= 200 and < 300,
            message = outcome.Message,
            workflow_run_id = outcome.WorkflowRunId,
        });
    }

    private async Task WriteIngestAuditAsync(
        Guid webhookId, string? eventName, bool signed, int bodyBytes,
        GitWebhookReceiver.Outcome outcome, CancellationToken ct)
    {
        var hook = await _db.GitWebhooks
            .AsNoTracking()
            .Where(w => w.GitWebhookId == webhookId)
            .Select(w => new { w.GitRepositoryId, w.Provider })
            .FirstOrDefaultAsync(ct);

        // Note: 429 is filtered by the rate limiter before the request
        // ever reaches this controller, so we don't need a `rate_limited`
        // mapping here. If the partition shape ever changes (e.g. moves
        // to a global limiter that lets the request through and tags it),
        // add the mapping back.
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
            repo_id = hook?.GitRepositoryId,
            provider = hook?.Provider,
            event_name = eventName,
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
                EntityType = "GitWebhook",
                EntityId = webhookId,
                Action = $"git_webhook.ingest.{status}",
                BeforeJson = JsonDocument.Parse("null").RootElement,
                AfterJson = JsonSerializer.SerializeToElement(payload),
                Actor = "git-webhook",
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
                "git.webhook.audit_failed webhook_id={Id} status={Status}",
                webhookId, status);
        }
    }
}
