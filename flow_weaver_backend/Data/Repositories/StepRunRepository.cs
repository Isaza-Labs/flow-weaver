using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class StepRunRepository : RepositoryBase<StepRun>, IStepRunRepository
{
    public StepRunRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<StepRun>> ListByRunAsync(
        Guid runId, int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.WorkflowRunId == runId)
            .OrderBy(s => s.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountByRunAsync(Guid runId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.WorkflowRunId == runId)
            .CountAsync(ct);

    public async Task<IReadOnlyList<StepRun>> ListByRunUnfilteredAsync(
        Guid runId, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .Where(s => s.WorkflowRunId == runId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StepOutputProjection>> ListOutputsByRunIdsAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0) return Array.Empty<StepOutputProjection>();
        return await Query(activeOnly: true, tracking: false)
            .Where(s => runIds.Contains(s.WorkflowRunId))
            .Select(s => new StepOutputProjection(s.WorkflowRunId, s.NodeId, s.Status, s.OutputPayload))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, SnippetRunStats>> GetCompletedStatsBySnippetAsync(
        IReadOnlyCollection<Guid> snippetIds, CancellationToken ct = default)
    {
        if (snippetIds.Count == 0)
            return new Dictionary<Guid, SnippetRunStats>();

        // One GROUP BY over the ids on this page — not a query per snippet.
        // activeOnly: false is intentional; see the interface comment.
        var rows = await Query(activeOnly: false, tracking: false)
            .Where(s => s.SnippetId != null
                        && snippetIds.Contains(s.SnippetId.Value)
                        && s.Status == Services.Engine.StepStatus.Completed)
            .GroupBy(s => s.SnippetId!.Value)
            .Select(g => new
            {
                SnippetId = g.Key,
                Count = g.Count(),
                // CompletedAt is nullable on the entity; Max over nulls yields
                // null, which the record carries through unchanged.
                Last = g.Max(s => s.CompletedAt),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.SnippetId,
            r => new SnippetRunStats(r.Count, r.Last));
    }
}
