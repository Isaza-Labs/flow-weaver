using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public sealed class AuditEventRepository(IServiceScopeFactory scopeFactory) : IAuditEventRepository
{
    public async Task AddAsync(AuditEvent entry, CancellationToken ct = default)
    {
        // Own DI scope, own AppDbContext. Sharing the caller's scoped context
        // made SaveChangesAsync here flush the CALLER's pending changes too:
        // WorkflowImportController stages integrations/snippets/actions and
        // commits them once at the end, but an audit write in the middle
        // persisted whatever was staged so far, so a later validation failure
        // left half an import behind with no rollback. Same reasoning as the
        // separate trace scope in WorkerHostedService.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
