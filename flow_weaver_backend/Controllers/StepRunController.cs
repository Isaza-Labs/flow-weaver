using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Per-id lookup for a step_run. Listing is always done through
// /api/run/{runId}/steps so every step query carries a parent-run check.
[ApiController]
[Route("api/steprun")]
[HasPermission("run.read")]
public class StepRunController : ControllerBase
{
    private readonly IStepRun _service;

    public StepRunController(IStepRun service)
    {
        _service = service;
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<StepRunResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);
}
