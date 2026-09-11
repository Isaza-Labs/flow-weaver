using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Refresh-token chain persistence. RefreshToken is keyed/queried by token hash
// and user, and has no IsActive soft-delete (it uses RevokedAt), so it stays
// off IRepository<T>. All lookups return tracked entities — the rotate/revoke
// flows mutate them in place.
public interface IRefreshTokenRepository
{
    void Add(RefreshToken entity);

    // By hash, any state (the rotate path inspects RevokedAt/ExpiresAt itself).
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    // By hash, only if not yet revoked (the revoke path).
    Task<RefreshToken?> FindActiveByHashAsync(string tokenHash, CancellationToken ct = default);

    // All non-revoked tokens for a user (chain revoke / reuse panic-button).
    Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
