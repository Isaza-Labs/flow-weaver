using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// The QA lab dashboard. Aggregates every qa-environment
// workflow with the status of its last run and whether it would be
// eligible for promotion to production right now (needs a completed
// run within the last 48h per PromotionService).
[ApiController]
[Route("api/qa")]
[HasPermission("qalab.read")]
public class QaLabController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public QaLabController(AppDbContext db, ICurrentUser caller)
    {
        _db = db;
        _caller = caller;
    }

    public sealed class QaDashboardRow
    {
        public Guid workflow_id { get; set; }
        public string name { get; set; } = string.Empty;
        public int version { get; set; }
        public DateTime updated_at { get; set; }
        public Guid? last_run_id { get; set; }
        public string? last_run_status { get; set; }
        public DateTime? last_run_completed_at { get; set; }
        public bool promotion_ready { get; set; }
    }

    public sealed class QaDashboardResponse
    {
        public int qa_device_count { get; set; }
        public int qa_pool_count { get; set; }
        public List<QaDashboardRow> workflows { get; set; } = new();
    }

    [HttpGet("dashboard")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<QaDashboardResponse>> Dashboard(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-48);

        // Pull every active qa workflow + last run in a single pass.
        // At small scale an N+1 wouldn't hurt, but the projection
        // is the same shape we'll want when we need to scale it.
        var workflows = await _db.Workflows
            .AsNoTracking()
            .Where(w => w.IsActive && w.Environment == "qa")
            .OrderByDescending(w => w.UpdatedAt)
            .Select(w => new
            {
                w.WorkflowId,
                w.Name,
                w.Version,
                w.UpdatedAt,
                LastRun = _db.WorkflowRuns
                    .AsNoTracking()
                    .Where(r => r.WorkflowId == w.WorkflowId)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new { r.WorkflowRunId, r.Status, r.CompletedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var qaDeviceCount = await _db.Devices
            .AsNoTracking()
            .CountAsync(d => d.IsActive && d.AllowQa, ct);
        var qaPoolCount = await _db.DevicePools
            .AsNoTracking()
            .CountAsync(p => p.IsActive && p.AllowQa, ct);

        var rows = workflows.Select(w => new QaDashboardRow
        {
            workflow_id = w.WorkflowId,
            name = w.Name,
            version = w.Version,
            updated_at = w.UpdatedAt,
            last_run_id = w.LastRun?.WorkflowRunId,
            last_run_status = w.LastRun?.Status,
            last_run_completed_at = w.LastRun?.CompletedAt,
            promotion_ready = w.LastRun != null
                && w.LastRun.Status == "completed"
                && w.LastRun.CompletedAt != null
                && w.LastRun.CompletedAt >= cutoff,
        }).ToList();

        return Ok(new QaDashboardResponse
        {
            qa_device_count = qaDeviceCount,
            qa_pool_count = qaPoolCount,
            workflows = rows,
        });
    }
}
