using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonEnvironments;
using flow_weaver_backend.Services.PythonEnvironments;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin-defined Python environments built at runtime with uv (see
// PythonEnvironment). Each one pins its own packages — e.g. `ssh-legacy`
// keeps paramiko < 5 for devices that only offer the ssh-rsa host key — and a
// Device (SSH runner) or Snippet (python_snippet / ansible_playbook) picks one.
//
// Writes are ADMIN-ONLY: defining or rebuilding an environment makes the worker
// download and install third-party packages, i.e. run third-party code — the
// same trust decision as approving a pip package on /api/admin/python-modules.
//
// Reads feed the environment pickers, so they are open to whoever may edit
// devices or snippets (plus `pythonenv.read`). Nothing secret is returned:
// URL credentials in requirements / resolved packages / errors are masked.
[ApiController]
[Route("api/python-environments")]
[Authorize]
public class PythonEnvironmentController : ControllerBase
{
    // Any one of these lets a caller read the list — the people who need the
    // picker. Admin passes every [HasPermission] policy anyway.
    internal static readonly string[] ReadCapabilities =
    {
        "pythonenv.read", "device.manage", "snippet.manage",
    };

    private readonly IPythonEnvironmentService _service;
    private readonly IAuthorizationService _authorization;

    public PythonEnvironmentController(IPythonEnvironmentService service, IAuthorizationService authorization)
    {
        _service = service;
        _authorization = authorization;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ListResponse<PythonEnvironmentResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        if (!await CanReadAsync()) return Forbid();
        return await _service.ListAsync(limit, offset);
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<PythonEnvironmentResponse>> GetById(Guid id)
    {
        if (!await CanReadAsync()) return Forbid();
        return await _service.GetAsync(id);
    }

    [HttpPost]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PythonEnvironmentResponse>> Create([FromBody] CreatePythonEnvironment dto)
        => _service.CreateAsync(dto);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PythonEnvironmentResponse>> Update(Guid id, [FromBody] UpdatePythonEnvironment dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> Delete(Guid id) => _service.DeleteAsync(id);

    [HttpPost("{id:guid}/rebuild")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<PythonEnvironmentResponse>> Rebuild(Guid id) => _service.RebuildAsync(id);

    // Any-of over ReadCapabilities, evaluated through the same permission
    // policies [HasPermission] uses (legacy tiers or granular grants).
    private async Task<bool> CanReadAsync()
    {
        foreach (var cap in ReadCapabilities)
        {
            var result = await _authorization.AuthorizeAsync(User, HasPermissionAttribute.PolicyPrefix + cap);
            if (result.Succeeded) return true;
        }
        return false;
    }
}
