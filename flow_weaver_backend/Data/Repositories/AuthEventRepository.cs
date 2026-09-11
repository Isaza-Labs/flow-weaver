using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public sealed class AuthEventRepository(IServiceScopeFactory scopeFactory) : IAuthEventRepository
{
    public async Task AddAsync(AuthEvent entry, CancellationToken ct = default)
    {
        // Own scope, for the reason spelled out in AuditEventRepository: an
        // append-only observability write must not decide when the caller's
        // business changes get committed. It matters here too — the login path
        // writes an AuthEvent while the User row's lockout counters are still
        // being mutated.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuthEvents.Add(entry);
        await db.SaveChangesAsync(ct);
    }
}
