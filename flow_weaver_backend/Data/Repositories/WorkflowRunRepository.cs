using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class WorkflowRunRepository : RepositoryBase<WorkflowRun>, IWorkflowRunRepository
{
    public WorkflowRunRepository(AppDbContext db) : base(db)
    {
    }

    // The three set-based statements below are `virtual` so a test double can
    // substitute them: the EF InMemory provider does not implement
    // ExecuteUpdateAsync. Everything else in the repository stays as written.
    public virtual Task<int> SoftDeleteStepsByRunAsync(Guid runId, DateTime now, CancellationToken ct = default)
        => Db.Set<StepRun>()
            .Where(s => s.WorkflowRunId == runId && s.IsActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.IsActive, false)
                .SetProperty(s => s.UpdatedAt, now), ct);

    public virtual async Task<(int runs, int steps)> SoftDeleteAllAsync(DateTime now, CancellationToken ct = default)
    {
        var runs = await Set
            .Where(r => r.IsActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.IsActive, false)
                .SetProperty(r => r.UpdatedAt, now), ct);

        var steps = await Db.Set<StepRun>()
            .Where(s => s.IsActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.IsActive, false)
                .SetProperty(s => s.UpdatedAt, now), ct);

        return (runs, steps);
    }

    public virtual Task<int> CancelInFlightStepsByRunAsync(Guid runId, DateTime now, CancellationToken ct = default)
        => Db.Set<StepRun>()
            .Where(s => s.WorkflowRunId == runId && s.IsActive && (s.Status == StepStatus.Pending || s.Status == StepStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, StepStatus.Cancelled)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.UpdatedAt, now), ct);

    public async Task<string?> GetEnvironmentByRunIdAsync(Guid workflowRunId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Join(Db.Set<Workflow>().AsNoTracking(),
                r => r.WorkflowId,
                w => w.WorkflowId,
                (r, w) => w.Environment)
            .FirstOrDefaultAsync(ct);

    public async Task<RunOriginRow?> GetRunOriginByRunIdAsync(Guid workflowRunId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Select(r => new RunOriginRow(r.WorkflowId, r.CreatedBy))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CompletedRunRow>> ListRecentCompletedAsync(
        Guid workflowId, DateTime since, int limit, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(r => r.WorkflowId == workflowId
                        && r.Status == RunStatus.Completed
                        && r.CompletedAt != null
                        && r.CompletedAt >= since)
            .OrderByDescending(r => r.CompletedAt)
            .Take(limit)
            .Select(r => new CompletedRunRow(r.WorkflowRunId, r.InputPayload, r.Status, r.CompletedAt))
            .ToListAsync(ct);
}
