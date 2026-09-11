using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class ThemeRepository : RepositoryBase<Theme>, IThemeRepository
{
    public ThemeRepository(AppDbContext db) : base(db)
    {
    }

    private IQueryable<Theme> VisibleTo(Guid userId)
        => Query(activeOnly: true, tracking: false)
            .Where(t => t.IsShared || t.OwnerUserId == userId);

    public async Task<IReadOnlyList<Theme>> ListVisibleToAsync(
        Guid userId, int limit, int offset, CancellationToken ct = default)
        => await VisibleTo(userId)
            .OrderByDescending(t => t.IsShared)
            .ThenByDescending(t => t.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountVisibleToAsync(Guid userId, CancellationToken ct = default)
        => await VisibleTo(userId).CountAsync(ct);

    public async Task<Theme?> FindByNameAsync(
        string name, bool isShared, Guid? ownerUserId, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false)
            .Where(t => t.IsShared == isShared && t.Name.ToLower() == name.ToLower());
        // Private names only collide within one owner's own list.
        if (!isShared)
            q = q.Where(t => t.OwnerUserId == ownerUserId);
        return await q.FirstOrDefaultAsync(ct);
    }
}
