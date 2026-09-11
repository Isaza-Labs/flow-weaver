using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Services.Job;

public class JobService : IJob
{
    private readonly IJobRepository _jobs;
    private readonly ICurrentUser _caller;
    private readonly ILogger<JobService> _logger;

    public JobService(IJobRepository jobs, ICurrentUser caller, ILogger<JobService> logger)
    {
        _jobs = jobs;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<JobResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _jobs.CountAsync();
        var jobs = await _jobs.ListAsync(limit, offset);

        _logger.LogDebug(
            "job.list.ok total={Total} returned={Returned}",
            total, jobs.Count);

        return new OkObjectResult(new ListResponse<JobResponse>
        {
            Data = jobs.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<JobResponse>> GetByIdAsync(Guid id)
    {
        var job = await _jobs.GetByIdAsync(id, tracking: false);
        if (job is null)
        {
            _logger.LogWarning("job.get.not_found job_id={JobId}", id);
            return new NotFoundObjectResult(new { error = "job not found" });
        }

        return ToResponse(job);
    }

    public async Task<ActionResult<Dictionary<string, int>>> GetQueueStatsAsync()
    {
        // Group by status. `pending`, `claimed`, `completed`, `failed` are
        // the documented lifecycle states in the plan — unknown values flow
        // through untouched so a new status added by the engine still
        // appears in the dashboard.
        var result = await _jobs.CountByStatusAsync();

        // Make sure the dashboard always has the four canonical keys even if
        // there are zero rows in a given state — avoids UI branches.
        foreach (var canonical in new[] { "pending", "claimed", "completed", "failed" })
        {
            result.TryAdd(canonical, 0);
        }

        _logger.LogDebug(
            "job.queue_stats.ok statuses={Statuses}",
            result.Count);

        return new OkObjectResult(result);
    }

    private static JobResponse ToResponse(JobModel j) => new()
    {
        Id = j.JobId,
        Type = j.Type,
        Payload = j.Payload,
        Tag = j.Tag,
        Priority = j.Priority,
        Status = j.Status,
        ClaimedBy = j.ClaimedBy,
        CreatedAt = j.CreatedAt,
        ClaimedAt = j.ClaimedAt,
        CompletedAt = j.CompletedAt,
    };
}
