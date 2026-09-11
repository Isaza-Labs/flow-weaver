using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class WorkflowTriggerRepository : RepositoryBase<WorkflowTrigger>, IWorkflowTriggerRepository
{
    public WorkflowTriggerRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<WorkflowTrigger>> ListByWorkflowAsync(
        Guid workflowId, int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(t => t.WorkflowId == workflowId)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountByWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(t => t.WorkflowId == workflowId)
            .CountAsync(ct);

    public async Task<WorkflowTrigger?> FindActiveByIdAsync(Guid triggerId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(t => t.WorkflowTriggerId == triggerId && t.IsActive, ct);

    public async Task<WorkflowTrigger?> FindTrackedByIdAsync(Guid triggerId, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(t => t.WorkflowTriggerId == triggerId, ct);

    // Case-insensitive + trimmed, like every sibling lookup here. A webhook
    // route is matched case-insensitively when a delivery arrives, so an
    // import that thought 'ABC123' was free would mint a second trigger on the
    // same live URL.
    public async Task<bool> RouteExistsAsync(string route, CancellationToken ct = default)
    {
        var needle = route.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .AnyAsync(t => t.Route != null && t.Route.ToLower() == needle, ct);
    }
}
