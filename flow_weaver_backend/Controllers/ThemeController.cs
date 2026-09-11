using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Themes;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Themes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// User-authored colour themes. Deliberately NOT admin-gated: a theme is a
// personal preference, and every authenticated user may keep their own. The
// one privileged action — publishing a theme to the whole org via
// `is_shared` — is enforced inside the service, so a non-admin gets a 403 on
// that field alone rather than losing the whole surface.
[ApiController]
[Route("api/themes")]
[Authorize]
public class ThemeController : ControllerBase
{
    private readonly IThemeService _service;

    public ThemeController(IThemeService service) => _service = service;

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<ThemeResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.ListAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ThemeResponse>> GetById(Guid id) => _service.GetAsync(id);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<ThemeResponse>> Create([FromBody] CreateTheme dto)
        => _service.CreateAsync(dto);

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<ThemeResponse>> Update(Guid id, [FromBody] UpdateTheme dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> Delete(Guid id) => _service.DeleteAsync(id);
}
