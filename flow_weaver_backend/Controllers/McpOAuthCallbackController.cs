using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Public OAuth redirect target for MCP authorization-code flows. The
// authorization server bounces the admin's browser here with ?code=&state=.
// Anonymous by necessity (no session on the redirect), but CSRF-safe: the
// signed, time-limited `state` identifies the server and its nonce is
// matched against the value stashed at oauth/start. Always ends in a 302 back
// to the frontend. Mirrors GitWebhookIngestController.
[ApiController]
[Route("api/mcp-servers")]
[AllowAnonymous]
public class McpOAuthCallbackController : ControllerBase
{
    private readonly IMcpOAuthFlowService _oauth;

    public McpOAuthCallbackController(IMcpOAuthFlowService oauth)
    {
        _oauth = oauth;
    }

    [HttpGet("{id:guid}/oauth/callback")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Callback(
        Guid id,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct = default)
    {
        var redirectUriFallback = $"{Request.Scheme}://{Request.Host}/api/mcp-servers/{id}/oauth/callback";
        var frontendUrl = await _oauth.HandleCallbackAsync(code, state, error, redirectUriFallback, ct);
        return Redirect(frontendUrl);
    }
}
