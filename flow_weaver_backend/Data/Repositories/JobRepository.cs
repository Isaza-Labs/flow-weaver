using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class JobRepository : RepositoryBase<Job>, IJobRepository
{
    public JobRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Dictionary<string, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        var rows = await Query(activeOnly: true, tracking: false)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Status, r => r.Count);
    }

    public async Task<int> CountByStatusesAsync(
        IReadOnlyCollection<string> statuses, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .Where(j => statuses.Contains(j.Status))
            .CountAsync(ct);
}
