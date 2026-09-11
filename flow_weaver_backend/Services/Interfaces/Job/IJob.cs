using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// Internal queue bookkeeping. The engine + workers (Sprint 2.2 / 2.4) are
// the only writers. The HTTP surface is strictly admin-only because the
// job payload can contain details of how workflows are dispatched and the
// queue depth is operational information.
//
// `GetQueueStatsAsync` returns a status → count map, used by the admin
// dashboard to spot stuck jobs.
public interface IJob
{
    Task<ActionResult<ListResponse<JobResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<JobResponse>> GetByIdAsync(Guid id);

    Task<ActionResult<Dictionary<string, int>>> GetQueueStatsAsync();
}
