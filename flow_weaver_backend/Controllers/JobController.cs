using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Admin-only view of the queue. Regular operators do not need job detail —
// they interact with runs/steps, which carry their mental model. Jobs
// expose payload shapes and worker-assignment internals that only an
// admin should see.
//
// Queue stats are the single exception: the dashboard shows them to every
// authenticated user (counts only, no payload leakage) so they override
// the controller-level Admin policy with Viewer.
[ApiController]
[Route("api/job")]
[HasPermission("job.read")]
public class JobController : ControllerBase
{
    private readonly IJob _service;

    public JobController(IJob service)
    {
        _service = service;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<JobResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<JobResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    // Aggregated counts by status for the dashboard. Always returns the
    // four canonical lifecycle keys (pending/claimed/completed/failed) so
    // the UI does not need to branch on missing buckets. Viewer-visible
    // because counts alone do not leak payloads.
    [HttpGet("queue/stats")]
    [HasPermission("job.stats.read")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<Dictionary<string, int>>> GetQueueStats()
        => _service.GetQueueStatsAsync();
}
