using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class WorkflowVersionRepository : RepositoryBase<WorkflowVersion>, IWorkflowVersionRepository
{
    public WorkflowVersionRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<WorkflowVersion>> ListByWorkflowAsync(
        Guid workflowId, int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(v => v.WorkflowId == workflowId)
            .OrderByDescending(v => v.Version)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountByWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(v => v.WorkflowId == workflowId)
            .CountAsync(ct);

    public async Task<WorkflowVersion?> GetByWorkflowAndVersionAsync(
        Guid workflowId, int version, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(v => v.WorkflowId == workflowId && v.Version == version)
            .FirstOrDefaultAsync(ct);
}
