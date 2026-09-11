using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Public inbound endpoint for messaging providers. Anonymous: authenticity is
// established by the provider's signature (verified per-provider inside the
// ingest service), never by a JWT. GET handles verification handshakes
// (WhatsApp hub.challenge); POST handles events. Responds fast — the agent runs
// asynchronously on the messaging worker.
[ApiController]
[Route("api/messaging/webhooks")]
[AllowAnonymous]
public class MessagingWebhookController : ControllerBase
{
    private readonly IMessagingIngestService _ingest;

    public MessagingWebhookController(IMessagingIngestService ingest) => _ingest = ingest;

    [HttpGet("{provider}/{channelId:guid}")]
    [HttpPost("{provider}/{channelId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.MessagingWebhookIngest)]
    public async Task<IActionResult> Ingest(string provider, Guid channelId, CancellationToken ct)
    {
        var request = await BuildRequestAsync(ct);
        var outcome = await _ingest.ReceiveAsync(provider, channelId, request, ct);

        // A verification challenge is echoed verbatim; everything else is a
        // small JSON status the provider ignores.
        if (outcome.Raw)
            return Content(outcome.Body ?? string.Empty, outcome.ContentType);

        return StatusCode(outcome.StatusCode, new { ok = outcome.StatusCode < 300, message = outcome.Body });
    }

    private async Task<MessagingHttpRequest> BuildRequestAsync(CancellationToken ct)
    {
        byte[] body = Array.Empty<byte>();
        if (HttpMethods.IsPost(Request.Method))
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct);
            body = ms.ToArray();
        }

        var headers = Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var query = Request.Query.ToDictionary(
            q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        return new MessagingHttpRequest
        {
            Method = Request.Method,
            Body = body,
            Headers = headers,
            Query = query,
        };
    }
}
