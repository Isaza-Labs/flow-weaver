using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// PlanFeature reads. Generic CRUD (Add/Save) resolves via the open-generic
// IRepository<PlanFeature> for create_workflow_plan; this adds the ordered
// checklist projection list_plan_features rebuilds on resume.
public interface IPlanFeatureRepository : IRepository<PlanFeature>
{
    // Active features scoped to a plan id and/or an import token
    // (both optional; the caller validates at least one is set),
    // ordered by Ordinal, projected to the checklist row. Read-only.
    Task<IReadOnlyList<PlanFeatureRow>> ListActiveAsync(
        Guid? planId, string? importToken, CancellationToken ct = default);
}

// list_plan_features checklist row.
public sealed record PlanFeatureRow(
    Guid PlanFeatureId, int Ordinal, string Title, string Status, string? SnippetType,
    string? VerifiedBy, DateTime? VerifiedAt, string? RejectionReason);
