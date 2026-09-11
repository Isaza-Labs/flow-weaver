using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public sealed class SloRepository(AppDbContext db) : ISloRepository
{
    public async Task<IReadOnlyList<SloRunSample>> GetCompletedRunsAsync(
        DateTime from, CancellationToken ct = default)
        => await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive
                && r.CreatedAt >= from
                && r.StartedAt != null
                && r.CompletedAt != null)
            .Select(r => new SloRunSample(r.Status, r.StartedAt!.Value, r.CompletedAt!.Value))
            .ToListAsync(ct);

    public async Task<int> CountCompletedJobsAsync(DateTime from, CancellationToken ct = default)
        => await db.Jobs.AsNoTracking()
            .Where(j => j.IsActive
                && j.CreatedAt >= from
                && j.Status == JobStatus.Completed)
            .CountAsync(ct);

    public async Task<IReadOnlyList<SloPromotionSample>> GetPromotionsAsync(
        DateTime from, CancellationToken ct = default)
        => await db.Workflows.AsNoTracking()
            .Where(w => w.IsActive
                && w.CreatedAt >= from
                && w.PromotedFrom != null
                && (w.Environment == "qa" || w.Environment == "production"))
            .Select(w => new SloPromotionSample(w.PromotedFrom, w.CreatedAt))
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, DateTime>> GetWorkflowCreatedAtAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<Guid, DateTime>();
        return await db.Workflows.AsNoTracking()
            .Where(w => ids.Contains(w.WorkflowId))
            .ToDictionaryAsync(w => w.WorkflowId, w => w.CreatedAt, ct);
    }
}
