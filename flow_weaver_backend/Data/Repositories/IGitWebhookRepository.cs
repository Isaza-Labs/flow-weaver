using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Git webhook + delivery persistence. The CRUD path is keyed by the parent
// repository (FK-scoped), and the anonymous receiver needs by-id lookups
// that skip that scoping — the parent repository is only known after the
// webhook row is loaded.
public interface IGitWebhookRepository : IRepository<GitWebhook>
{
    // Active webhooks under a repository, name-ordered. Read-only.
    Task<IReadOnlyList<GitWebhook>> ListByRepoAsync(
        Guid repositoryId, CancellationToken ct = default);

    // Single active webhook scoped to its parent repository, tracked so the
    // caller can mutate + save it.
    Task<GitWebhook?> FindByRepoAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct = default);

    // Most-recent deliveries for a webhook (newest first), capped at limit.
    Task<IReadOnlyList<GitWebhookDelivery>> ListDeliveriesAsync(
        Guid webhookId, int limit, CancellationToken ct = default);

    // Out-of-band (anonymous receiver): resolve an active webhook by id alone
    // — the parent repository is unknown until the row is read. Read-only.
    Task<GitWebhook?> FindActiveByIdAsync(Guid webhookId, CancellationToken ct = default);

    // Tracked webhook by id alone (no active filter) so the receiver can
    // touch LastDelivery* after persisting a delivery row.
    Task<GitWebhook?> FindTrackedByIdAsync(Guid webhookId, CancellationToken ct = default);

    // Stage a delivery row for the next SaveChangesAsync.
    void AddDelivery(GitWebhookDelivery delivery);
}
