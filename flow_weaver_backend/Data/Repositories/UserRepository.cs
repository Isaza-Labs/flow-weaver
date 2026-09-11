using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class UserRepository : RepositoryBase<User>, IUserRepository
{
    public UserRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Dictionary<Guid, string>> GetUsernamesByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, string>();
        return await Set.AsNoTracking()
            .Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.Username })
            .ToDictionaryAsync(u => u.UserId, u => u.Username, ct);
    }

    public async Task<User?> FindActiveByUsernameAsync(
        string username, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(
            u => u.Username == username && u.IsActive, ct);

    public async Task<User?> FindActiveByIdAsync(Guid userId, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
}

