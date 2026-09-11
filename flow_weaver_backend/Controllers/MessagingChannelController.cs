using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin management of messaging channels: connect a provider, rotate tokens,
// view delivery audit, and revoke account links. Inbound webhooks are handled
// by the public MessagingWebhookController (F2), not here.
[ApiController]
[Route("api/messaging/channels")]
[Authorize(Policy = "Admin")]
public class MessagingChannelController : ControllerBase
{
    private readonly IMessagingChannelService _service;

    public MessagingChannelController(IMessagingChannelService service) => _service = service;

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<MessagingChannelResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.ListAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<MessagingChannelResponse>> GetOne(Guid id) => _service.GetAsync(id);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<MessagingChannelResponse>> Create([FromBody] CreateMessagingChannel dto)
        => _service.CreateAsync(dto);

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<MessagingChannelResponse>> Update(Guid id, [FromBody] UpdateMessagingChannel dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> Delete(Guid id) => _service.DeleteAsync(id);

    [HttpGet("{id:guid}/deliveries")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<MessagingChannelActivityResponse>> Deliveries(Guid id, [FromQuery] int limit = 50)
        => _service.GetActivityAsync(id, limit);

    [HttpGet("{id:guid}/links")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<MessagingIdentityLinkResponse>>> Links(Guid id)
        => _service.ListLinksAsync(id);

    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> RevokeLink(Guid id, Guid linkId) => _service.RevokeLinkAsync(id, linkId);
}
