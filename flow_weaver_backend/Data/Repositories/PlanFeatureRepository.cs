using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class PlanFeatureRepository : RepositoryBase<PlanFeature>, IPlanFeatureRepository
{
    public PlanFeatureRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<PlanFeatureRow>> ListActiveAsync(
        Guid? planId, string? importToken, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        if (planId is not null)
            q = q.Where(f => f.WorkflowPlanId == planId);
        if (importToken is not null)
            q = q.Where(f => f.ImportToken == importToken);
        return await q
            .OrderBy(f => f.Ordinal)
            .Select(f => new PlanFeatureRow(
                f.PlanFeatureId, f.Ordinal, f.Title, f.Status, f.SnippetType,
                f.VerifiedBy, f.VerifiedAt, f.RejectionReason))
            .ToListAsync(ct);
    }
}
