using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public sealed class RefreshTokenRepository(AppDbContext db) : IRefreshTokenRepository
{
    public void Add(RefreshToken entity) => db.RefreshTokens.Add(entity);

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
        => await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<RefreshToken?> FindActiveByHashAsync(string tokenHash, CancellationToken ct = default)
        => await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.RevokedAt == null, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
        => await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
