using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class WorkflowAcceptanceTestRepository
    : RepositoryBase<WorkflowAcceptanceTest>, IWorkflowAcceptanceTestRepository
{
    public WorkflowAcceptanceTestRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<WorkflowAcceptanceTest>> ListTrackedByWorkflowAsync(
        Guid workflowId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: true)
            .Where(t => t.WorkflowId == workflowId)
            .ToListAsync(ct);
}
