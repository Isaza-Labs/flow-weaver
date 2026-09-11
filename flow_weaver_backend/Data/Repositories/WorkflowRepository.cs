using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class WorkflowRepository : RepositoryBase<Workflow>, IWorkflowRepository
{
    public WorkflowRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Workflow?> GetLatestPromotedFromAsync(
        Guid sourceWorkflowId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(w => w.PromotedFrom == sourceWorkflowId)
            .OrderByDescending(w => w.PromotedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<Workflow?> FindLatestByNameInEnvironmentAsync(
        string environment, string name, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(w => w.Environment == environment && w.Name == name)
            .OrderByDescending(w => w.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<WorkflowShape>> ListActiveShapesAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Select(w => new WorkflowShape
            {
                WorkflowId = w.WorkflowId,
                Name = w.Name,
                Environment = w.Environment,
                Nodes = w.Nodes,
                Edges = w.Edges,
            })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<WorkflowSummary>> ListSummariesAsync(
        string? environment, int limit, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        if (!string.IsNullOrEmpty(environment))
            q = q.Where(w => w.Environment == environment);
        return await q
            .OrderByDescending(w => w.CreatedAt)
            .Take(limit)
            .Select(w => new WorkflowSummary(w.WorkflowId, w.Name, w.Environment, w.Version))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkflowSummary>> FindActiveByNameAsync(
        string name, int limit, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(w => w.Name == name)
            .Take(limit)
            .Select(w => new WorkflowSummary(w.WorkflowId, w.Name, w.Environment, w.Version))
            .ToListAsync(ct);
}
