using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Self-service account linking. Authenticated (any signed-in user): the user
// opens the link the bot DM'd them, sees which external identity they're about
// to bind, and confirms — binding it to THEIR account.
[ApiController]
[Route("api/messaging/link")]
[HasPermission("messaging.link")]
public class MessagingLinkController : ControllerBase
{
    private readonly IMessagingLinkService _service;

    public MessagingLinkController(IMessagingLinkService service) => _service = service;

    // Preview the pending link (provider, channel, external user) before confirming.
    [HttpGet("{token}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<MessagingLinkPreviewResponse>> Preview(string token)
        => _service.PreviewAsync(token);

    // Bind the external identity to the authenticated user.
    [HttpPost("{token}/confirm")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<MessagingLinkConfirmResponse>> Confirm(string token)
        => _service.ConfirmAsync(token);
}
