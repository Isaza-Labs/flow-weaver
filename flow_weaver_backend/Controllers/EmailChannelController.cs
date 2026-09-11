using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Email;
using flow_weaver_backend.Services.Email;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Outbound email configuration: register an SMTP relay per provider, rotate its
// password, and fire a live test message. Workflow steps send through these
// channels via the `email_send` snippet — they never carry SMTP settings.
[ApiController]
[Route("api/email/channels")]
[HasPermission("email.read")]
public class EmailChannelController : ControllerBase
{
    private readonly IEmailChannelService _service;

    public EmailChannelController(IEmailChannelService service) => _service = service;

    // Connection defaults for the "new channel" form. Read-only and free of
    // any deployment secret, so it rides the same email.read gate as the list.
    [HttpGet("presets")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public ActionResult<ListResponse<EmailProviderPresetResponse>> Presets() => _service.ListPresets();

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<EmailChannelResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.ListAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<EmailChannelResponse>> GetOne(Guid id) => _service.GetAsync(id);

    [HttpPost]
    [HasPermission("email.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<EmailChannelResponse>> Create([FromBody] CreateEmailChannel dto)
        => _service.CreateAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("email.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<EmailChannelResponse>> Update(Guid id, [FromBody] UpdateEmailChannel dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("email.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> Delete(Guid id) => _service.DeleteAsync(id);

    // Live send. Gated on email.send rather than email.manage so an operator
    // can verify a relay without being able to repoint it.
    [HttpPost("{id:guid}/test")]
    [HasPermission("email.send")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<TestEmailChannelResponse>> Test(
        Guid id, [FromBody] TestEmailChannelRequest dto, CancellationToken ct = default)
        => _service.TestAsync(id, dto, ct);
}
