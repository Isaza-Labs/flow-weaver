using System.Text.Json;
using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Security;

/// <summary>
/// Encrypts integration auth configs that were stored before
/// <see cref="IntegrationAuthCipher"/> existed.
/// </summary>
/// <remarks>
/// Reading a legacy row yields its plaintext unchanged, so re-saving it with the
/// property marked modified is enough: the context's converter writes it back as
/// an envelope. Idempotent: on PostgreSQL only rows whose stored jsonb is an
/// object without the envelope key are touched, so a second run finds nothing.
/// </remarks>
public static class IntegrationAuthBackfill
{
    private const string PlaintextIdsSql =
        "SELECT \"IntegrationId\" AS \"Value\" FROM integrations " +
        "WHERE jsonb_typeof(\"AuthConfig\") = 'object' AND NOT jsonb_exists(\"AuthConfig\", '$enc')";

    public static async Task RunAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            var count = await EncryptPlaintextAsync(db);
            if (count > 0)
                logger.LogInformation("integration.auth.backfill encrypted={Count}", count);
        }
        catch (Exception ex)
        {
            // Never block startup: an unencrypted row still works, it is just not protected yet.
            logger.LogError(ex, "integration.auth.backfill.failed");
        }
    }

    public static async Task<int> EncryptPlaintextAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!db.EncryptsIntegrationAuth) return 0;

        List<Guid> ids;
        if (db.Database.IsRelational())
        {
            ids = await db.Database.SqlQueryRaw<Guid>(PlaintextIdsSql).ToListAsync(ct);
        }
        else
        {
            // Non-relational providers (tests) can't inspect the stored value;
            // re-saving every object config is still correct, just not minimal.
            ids = (await db.Integrations.IgnoreQueryFilters()
                    .Select(i => new { i.IntegrationId, i.AuthConfig })
                    .ToListAsync(ct))
                .Where(i => i.AuthConfig.ValueKind == JsonValueKind.Object)
                .Select(i => i.IntegrationId)
                .ToList();
        }
        if (ids.Count == 0) return 0;

        var rows = await db.Integrations.IgnoreQueryFilters()
            .Where(i => ids.Contains(i.IntegrationId))
            .ToListAsync(ct);
        foreach (var row in rows)
            db.Entry(row).Property(i => i.AuthConfig).IsModified = true;

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
