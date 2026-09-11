using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Messaging channel persistence. The CRUD surface comes from the base
// methods; the anonymous webhook receiver needs by-id lookups that skip the
// active filter, since the channel is only known once its row is loaded.
public interface IMessagingChannelRepository : IRepository<MessagingChannel>
{
    // Out-of-band (anonymous receiver): resolve an active channel by id alone.
    // Read-only.
    Task<MessagingChannel?> FindActiveByIdAsync(Guid channelId, CancellationToken ct = default);

    // Tracked channel by id alone (no active filter) so the receiver can
    // touch LastDelivery* after a delivery.
    Task<MessagingChannel?> FindTrackedByIdAsync(Guid channelId, CancellationToken ct = default);

    // Enabled channels of a provider (admin listings / lookups).
    Task<IReadOnlyList<MessagingChannel>> ListByProviderAsync(
        string provider, CancellationToken ct = default);

    // All enabled, active channels of a provider — for the Slack Socket Mode
    // connection manager, which runs in the background with no request
    // context. Read-only.
    Task<IReadOnlyList<MessagingChannel>> ListEnabledByProviderAllTenantsAsync(
        string provider, CancellationToken ct = default);
}
