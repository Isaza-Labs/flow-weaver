using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.PythonModules;
using flow_weaver_backend.Services.PythonModules;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin management of the python_snippet import allow-list. Adding a
// pip module here makes the worker install it; only `ready` modules become
// importable. Admin-only — the allow-list relaxes a sandbox boundary, so the
// admin is the final barrier (the static exec/eval/__import__/open bans still
// apply regardless).
[ApiController]
[Route("api/admin/python-modules")]
[Authorize(Policy = "Admin")]
public class AllowedPythonModuleController : ControllerBase
{
    private readonly IAllowedPythonModuleService _service;

    public AllowedPythonModuleController(IAllowedPythonModuleService service) => _service = service;

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<AllowedPythonModuleResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.ListAsync(limit, offset);

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AllowedPythonModuleResponse>> Create([FromBody] CreateAllowedPythonModule dto)
        => _service.CreateAsync(dto);

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<IActionResult> Delete(Guid id) => _service.DeleteAsync(id);

    [HttpPost("{id:guid}/retry")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AllowedPythonModuleResponse>> Retry(Guid id) => _service.RetryAsync(id);
}
