using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// One-time account-linking tokens. Minted by the ingest path, consumed by the
// authenticated confirm endpoint.
public interface IMessagingLinkTokenRepository : IRepository<MessagingLinkToken>
{
    // Tracked, unconsumed, unexpired token matching a presented hash, or null.
    // `nowUtc` is passed in so the caller controls the clock (and tests stay
    // deterministic).
    Task<MessagingLinkToken?> FindActiveByHashAsync(
        byte[] tokenHash, DateTime nowUtc, CancellationToken ct = default);

    // Atomically consume a token (single-use). Returns true only if THIS call
    // won the race (the row was still unconsumed). Guards against two concurrent
    // confirms of the same token.
    Task<bool> TryConsumeAsync(
        Guid tokenId, Guid consumedByUserId, DateTime nowUtc, CancellationToken ct = default);

    // Retention sweep: delete tokens already past expiry.
    Task<int> DeleteExpiredOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
