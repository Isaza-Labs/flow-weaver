using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class GitWebhookRepository : RepositoryBase<GitWebhook>, IGitWebhookRepository
{
    public GitWebhookRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<GitWebhook>> ListByRepoAsync(
        Guid repositoryId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(w => w.GitRepositoryId == repositoryId)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

    public async Task<GitWebhook?> FindByRepoAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: true)
            .FirstOrDefaultAsync(
                w => w.GitWebhookId == webhookId && w.GitRepositoryId == repositoryId, ct);

    public async Task<IReadOnlyList<GitWebhookDelivery>> ListDeliveriesAsync(
        Guid webhookId, int limit, CancellationToken ct = default)
        => await Db.Set<GitWebhookDelivery>().AsNoTracking()
            .Where(d => d.GitWebhookId == webhookId)
            .OrderByDescending(d => d.At)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<GitWebhook?> FindActiveByIdAsync(Guid webhookId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(w => w.GitWebhookId == webhookId && w.IsActive, ct);

    public async Task<GitWebhook?> FindTrackedByIdAsync(Guid webhookId, CancellationToken ct = default)
        => await Set.FirstOrDefaultAsync(w => w.GitWebhookId == webhookId, ct);

    public void AddDelivery(GitWebhookDelivery delivery) =>
        Db.Set<GitWebhookDelivery>().Add(delivery);
}
