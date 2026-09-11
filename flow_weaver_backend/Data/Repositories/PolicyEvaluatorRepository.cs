using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public sealed class PolicyEvaluatorRepository(AppDbContext db) : IPolicyEvaluatorRepository
{
    public async Task<IReadOnlyList<PolicyRuleRow>> GetEnabledPoliciesAsync(
        CancellationToken ct = default)
    {
        var rows = await db.Policies
            .AsNoTracking()
            .Where(p => p.IsActive && p.Enabled)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { p.PolicyId, p.Name, p.Rule })
            .ToListAsync(ct);
        return rows.Select(r => new PolicyRuleRow(r.PolicyId, r.Name, r.Rule)).ToList();
    }

    public async Task<int> CountCompletedRunsAsync(
        Guid? workflowId, DateTime? completedSince, CancellationToken ct = default)
    {
        var q = db.WorkflowRuns.AsNoTracking()
            .Where(r => r.Status == "completed");
        if (workflowId is not null)
            q = q.Where(r => r.WorkflowId == workflowId.Value);
        if (completedSince is not null)
            q = q.Where(r => r.CompletedAt != null && r.CompletedAt >= completedSince);
        return await q.CountAsync(ct);
    }

    public async Task<DateTime?> GetLastCompletedRunAsync(
        Guid? workflowId, CancellationToken ct = default)
    {
        var q = db.WorkflowRuns.AsNoTracking()
            .Where(r => r.Status == "completed" && r.CompletedAt != null);
        if (workflowId is not null)
            q = q.Where(r => r.WorkflowId == workflowId.Value);
        return await q.MaxAsync(r => (DateTime?)r.CompletedAt, ct);
    }

    public async Task<int> CountCompletedStepRunsAsync(
        IReadOnlyList<Guid> snippetIds, DateTime? completedSince, CancellationToken ct = default)
    {
        var q = db.StepRuns.AsNoTracking()
            .Where(s => s.Status == "completed");
        if (snippetIds.Count > 0)
            q = q.Where(s => s.SnippetId.HasValue && snippetIds.Contains(s.SnippetId.Value));
        if (completedSince is not null)
            q = q.Where(s => s.CompletedAt != null && s.CompletedAt >= completedSince);
        return await q.CountAsync(ct);
    }
}
