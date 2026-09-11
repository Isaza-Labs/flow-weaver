using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Common;

/// <summary>
/// Assigns a <see cref="Slug"/> to Integration / Snippet rows that predate the
/// column.
/// </summary>
/// <remarks>
/// <para>
/// Runs at boot rather than inside the EF migration on purpose: allocating a
/// slug means deriving it from a name AND resolving collisions against
/// everything already taken, which is real logic with real edge cases (two
/// integrations both called "NetBox", a name that is entirely punctuation). That
/// belongs in tested C#, not in hand-written SQL inside a migration where it
/// cannot be exercised.
/// </para>
/// <para>
/// Idempotent: only rows with a null slug are touched, so a restart is a no-op
/// and a row created after the backfill keeps the slug its service assigned.
/// </para>
/// </remarks>
public static class SlugBackfillService
{
    public static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var integrations = await BackfillIntegrationsAsync(db);
            var snippets = await BackfillSnippetsAsync(db);

            if (integrations + snippets > 0)
                logger.LogInformation(
                    "slug.backfill.ok integrations={Integrations} snippets={Snippets}",
                    integrations, snippets);
        }
        catch (Exception ex)
        {
            // Never block boot. A missing slug degrades cross-instance import
            // (the row simply can't be matched by identity yet); it does not
            // break anything that worked before the column existed.
            logger.LogError(ex, "slug.backfill.failed — rows without a slug stay unmatched on import");
        }
    }

    private static async Task<int> BackfillIntegrationsAsync(AppDbContext db)
    {
        // Includes soft-deleted rows: the unique index spans them, so a deleted
        // row holding "netbox" must be counted as taken.
        var rows = await db.Integrations.ToListAsync();
        var taken = rows.Where(r => r.Slug is not null)
            .Select(r => r.Slug!)
            .ToHashSet(StringComparer.Ordinal);

        var assigned = 0;
        // Oldest first so the original row keeps the clean slug and a later
        // duplicate gets the -2 suffix, which is the least surprising outcome
        // for anyone who already shared a bundle referring to the original.
        foreach (var row in rows.Where(r => r.Slug is null).OrderBy(r => r.CreatedAt))
        {
            row.Slug = Slug.Unique(row.Name, taken);
            taken.Add(row.Slug);
            assigned++;
        }

        if (assigned > 0) await db.SaveChangesAsync();
        return assigned;
    }

    private static async Task<int> BackfillSnippetsAsync(AppDbContext db)
    {
        var rows = await db.Snippets.ToListAsync();
        var taken = rows.Where(r => r.Slug is not null)
            .Select(r => r.Slug!)
            .ToHashSet(StringComparer.Ordinal);

        var assigned = 0;
        foreach (var row in rows.Where(r => r.Slug is null).OrderBy(r => r.CreatedAt))
        {
            row.Slug = Slug.Unique(row.Name, taken);
            taken.Add(row.Slug);
            assigned++;
        }

        if (assigned > 0) await db.SaveChangesAsync();
        return assigned;
    }
}
