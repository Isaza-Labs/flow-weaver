using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// User persistence. Minimal today (display-name lookups for audit/permission
// responses); extended in the auth phase with login/refresh lookups.
public interface IUserRepository : IRepository<User>
{
    // Usernames for a set of user ids, keyed by id.
    Task<Dictionary<Guid, string>> GetUsernamesByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    // Active user by username — the unique key. Tracked (login mutates
    // lockout / login bookkeeping).
    Task<User?> FindActiveByUsernameAsync(
        string username, CancellationToken ct = default);

    // Active user by id (change-password / me). Tracked.
    Task<User?> FindActiveByIdAsync(Guid userId, CancellationToken ct = default);
}
