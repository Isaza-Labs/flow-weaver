using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Inbound-event + outbound-delivery audit for messaging channels. One repository
// covers both append-only tables (base entity = MessagingDelivery); inbound rows
// are reached through the sibling DbSet, mirroring how GitWebhookRepository
// handles GitWebhookDelivery. Inbound rows double as the idempotency ledger.
public interface IMessagingDeliveryRepository : IRepository<MessagingDelivery>
{
    // Idempotency: true if this provider event id was already accepted for the
    // channel.
    Task<bool> InboundExistsAsync(
        Guid channelId, string providerEventId, CancellationToken ct = default);

    // Stage an inbound audit row for the next SaveChangesAsync.
    void AddInbound(MessagingInboundEvent inbound);

    // Stage an outbound delivery row for the next SaveChangesAsync.
    void AddDelivery(MessagingDelivery delivery);

    // Most-recent inbound events for a channel (admin view), newest first.
    Task<IReadOnlyList<MessagingInboundEvent>> ListInboundAsync(
        Guid channelId, int limit, CancellationToken ct = default);

    // Most-recent outbound deliveries for a channel (admin view), newest first.
    Task<IReadOnlyList<MessagingDelivery>> ListDeliveriesAsync(
        Guid channelId, int limit, CancellationToken ct = default);

    // Atomically claim an inbound event for processing (queued → processing).
    // Returns true only if THIS call won (the row was still 'queued'); a
    // reclaimed/duplicate job sees false and must skip the agent run. This is
    // the at-most-once gate for turn processing.
    Task<bool> TryClaimInboundForProcessingAsync(
        Guid inboundEventId, DateTime nowUtc, CancellationToken ct = default);

    // Best-effort terminal status update for an inbound event (completed/failed).
    Task MarkInboundStatusAsync(
        Guid inboundEventId, string status, DateTime nowUtc, CancellationToken ct = default);

    // Retention sweep: delete audit rows older than the cutoff.
    // Returns the rows removed.
    Task<int> DeleteInboundOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
    Task<int> DeleteDeliveriesOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
