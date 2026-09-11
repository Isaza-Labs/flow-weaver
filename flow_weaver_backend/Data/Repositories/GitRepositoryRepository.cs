using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class GitRepositoryRepository : RepositoryBase<GitRepository>, IGitRepositoryRepository
{
    public GitRepositoryRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<GitRepository>> ListOrderedByNameAsync(
        int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .OrderBy(r => r.Name)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

    public async Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .AnyAsync(r => r.Name == name, ct);

    public async Task<GitRepository?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(r => r.Name.ToLower() == needle, ct);
    }
}
