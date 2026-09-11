using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Security.Authorization;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/mcp-servers")]
[HasPermission("mcpserver.read")]
public class McpServerController : ControllerBase
{
    private readonly IMcpServerService _service;
    private readonly IMcpOAuthFlowService _oauth;
    private readonly ICurrentUser _caller;

    public McpServerController(IMcpServerService service, IMcpOAuthFlowService oauth, ICurrentUser caller)
    {
        _service = service;
        _oauth = oauth;
        _caller = caller;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<McpServerResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
        => _service.ListAsync(limit, offset, ct);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<McpServerResponse>> GetById(Guid id, CancellationToken ct = default)
        => _service.GetAsync(id, ct);

    [HttpPost]
    [HasPermission("mcpserver.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<McpServerResponse>> Post([FromBody] CreateMcpServerRequest dto, CancellationToken ct = default)
        => _service.CreateAsync(dto, ct);

    [HttpPut("{id:guid}")]
    [HasPermission("mcpserver.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<McpServerResponse>> Update(Guid id, [FromBody] UpdateMcpServerRequest dto, CancellationToken ct = default)
        => _service.UpdateAsync(id, dto, ct);

    [HttpDelete("{id:guid}")]
    [HasPermission("mcpserver.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<McpServerResponse>> Delete(Guid id, CancellationToken ct = default)
        => _service.DeleteAsync(id, ct);

    // All cached tools across every server (builder palette / detail).
    [HttpGet("tools")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<McpToolResponse>>> GetAllTools(CancellationToken ct = default)
        => _service.ListAllToolsAsync(ct);

    // Cached tools of one server.
    [HttpGet("{id:guid}/tools")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<McpToolResponse>>> GetTools(
        Guid id, [FromQuery] int limit = 200, [FromQuery] int offset = 0, CancellationToken ct = default)
        => _service.ListToolsAsync(id, limit, offset, ct);

    // Test the connection and (re)sync the tool cache. Mutates Status → manage.
    [HttpPost("{id:guid}/sync")]
    [HasPermission("mcpserver.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<McpSyncResult>> Sync(Guid id, CancellationToken ct = default)
        => _service.TestAndSyncAsync(id, ct);

    // Begin the OAuth authorization-code flow: returns the URL the browser must
    // visit to consent. The callback (see McpOAuthCallbackController) completes it.
    [HttpPost("{id:guid}/oauth/start")]
    [HasPermission("mcpserver.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<McpOAuthStartResponse>> OAuthStart(Guid id, CancellationToken ct = default)
    {
        var redirectUri = $"{Request.Scheme}://{Request.Host}/api/mcp-servers/{id}/oauth/callback";
        var result = await _oauth.StartAsync(id, redirectUri, ct);
        if (result.Error is not null)
            return new BadRequestObjectResult(new { error = result.Error });
        return new McpOAuthStartResponse { AuthorizationUrl = result.AuthorizationUrl! };
    }
}
