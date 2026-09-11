using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class SnippetRepository : RepositoryBase<Snippet>, ISnippetRepository
{
    public SnippetRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<HashSet<string>> ListTakenSlugsAsync(CancellationToken ct = default)
        // activeOnly:false — the unique index spans soft-deleted rows too.
        => (await Query(activeOnly: false, tracking: false)
                .Where(s => s.Slug != null)
                .Select(s => s.Slug!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task<Snippet?> FindActiveBySlugAsync(string slug, CancellationToken ct = default)
    {
        // Trimmed + case-folded like every sibling lookup: a bundle's slug is
        // hand-editable text, and " Collect" must not read as a different
        // snippet from "collect" (bundle/SPEC.md 5.1).
        var needle = slug.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(s => s.Slug != null && s.Slug.ToLower() == needle, ct);
    }

    // Exact, case-insensitive, trimmed (bundle/SPEC.md 5.1). AsNoTracking: an
    // import RESOLVES against this row and must not attach it to the change
    // tracker, where the importer's next SaveChanges would flush it.
    public async Task<Snippet?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(s => s.Name.ToLower() == needle, ct);
    }

    public async Task<IReadOnlyList<SnippetSummary>> ListActiveSummariesAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Select(s => new SnippetSummary(s.SnippetId, s.Name, s.Type))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SnippetCatalogRow>> ListActiveCatalogAsync(
        string? type, int limit, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        if (!string.IsNullOrEmpty(type))
            q = q.Where(s => s.Type == type);

        // Proven snippets first, then by name. Ordering matters as much as the
        // flag: the agent reads top-down and the list is capped, so a working
        // snippet must never be pushed past the cap by alphabetically-earlier
        // drafts.
        //
        // The EXISTS subquery is deliberate — a join would multiply rows by
        // step count, and a second round-trip would have to be stitched in
        // memory after Take() had already cut the wrong ones.
        // Anonymous intermediate projection: EF cannot translate ordering over
        // a member of a constructor-projected record, but it can bind members
        // of an anonymous type.
        return await q
            .Select(s => new
            {
                s.SnippetId, s.Name, s.Type, s.Description,
                Proven = Db.StepRuns.Any(r => r.SnippetId == s.SnippetId
                                              && r.Status == Services.Engine.StepStatus.Completed)
            })
            .OrderByDescending(x => x.Proven)
            .ThenBy(x => x.Name)
            .Take(limit)
            .Select(x => new SnippetCatalogRow(
                x.SnippetId, x.Name, x.Type, x.Description, x.Proven))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SnippetIntakeRow>> ListActiveIntakeByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<SnippetIntakeRow>();
        return await Query(activeOnly: true, tracking: false)
            .Where(s => ids.Contains(s.SnippetId))
            .Select(s => new SnippetIntakeRow(s.SnippetId, s.Name, s.Type, s.TargetMode, s.InputSchema))
            .ToListAsync(ct);
    }

    public async Task<HashSet<Guid>> ExistingActiveIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new HashSet<Guid>();
        return (await Query(activeOnly: true, tracking: false)
            .Where(s => ids.Contains(s.SnippetId))
            .Select(s => s.SnippetId)
            .ToListAsync(ct))
            .ToHashSet();
    }
}
