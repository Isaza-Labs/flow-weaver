using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class PythonEnvironmentRepository
    : RepositoryBase<PythonEnvironment>, IPythonEnvironmentRepository
{
    public PythonEnvironmentRepository(AppDbContext db) : base(db)
    {
    }

    // Alphabetical: the pickers and the admin page list environments by name.
    public override async Task<IReadOnlyList<PythonEnvironment>> ListAsync(
        int limit, int offset, bool activeOnly = true, CancellationToken ct = default)
        => await Query(activeOnly, tracking: false)
            .OrderBy(e => e.Name)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<PythonEnvironment?> FindActiveByNameAsync(string name, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(e => e.Name == name, ct);

    public async Task<PythonEnvironment?> FindActiveSshFallbackAsync(
        Guid? excludeId = null, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(e => e.IsSshFallback && (excludeId == null || e.PythonEnvironmentId != excludeId))
            .OrderBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<(int Devices, int Snippets)> CountReferencesAsync(Guid id, CancellationToken ct = default)
    {
        var devices = await Db.Set<Device>().AsNoTracking()
            .CountAsync(d => d.IsActive && d.PythonEnvironmentId == id, ct);
        var snippets = await Db.Set<Snippet>().AsNoTracking()
            .CountAsync(s => s.IsActive && s.PythonEnvironmentId == id, ct);
        return (devices, snippets);
    }

    public async Task<PythonEnvironment?> ClaimNextForBuildAsync(
        DateTime staleBefore, CancellationToken ct = default)
    {
        var candidates = await Set.AsNoTracking()
            .Where(e => e.IsActive
                && (e.Status == PythonEnvironment.StatusPending
                    || (e.Status == PythonEnvironment.StatusBuilding && e.UpdatedAt < staleBefore)))
            .OrderBy(e => e.UpdatedAt)
            .Select(e => e.PythonEnvironmentId)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            var now = DateTime.UtcNow;
            // Conditional UPDATE: only the process that flips the row from a
            // claimable state to `building` wins. Others get 0 rows affected.
            var claimed = await Set
                .Where(e => e.PythonEnvironmentId == id
                    && e.IsActive
                    && (e.Status == PythonEnvironment.StatusPending
                        || (e.Status == PythonEnvironment.StatusBuilding && e.UpdatedAt < staleBefore)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, PythonEnvironment.StatusBuilding)
                    .SetProperty(e => e.UpdatedAt, now), ct);
            if (claimed == 1)
                return await Set.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.PythonEnvironmentId == id, ct);
        }
        return null;
    }

    public async Task<bool> MarkReadyAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string resolvedPackages, string requirementsHash, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var updated = await Set
            .Where(e => e.PythonEnvironmentId == id
                && e.Status == PythonEnvironment.StatusBuilding
                && e.Requirements == claimedRequirements
                && e.PythonVersion == claimedPythonVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, PythonEnvironment.StatusReady)
                .SetProperty(e => e.ResolvedPackages, resolvedPackages)
                .SetProperty(e => e.RequirementsHash, requirementsHash)
                .SetProperty(e => e.LastError, (string?)null)
                .SetProperty(e => e.BuiltAt, now)
                .SetProperty(e => e.UpdatedAt, now), ct);
        return updated > 0;
    }

    public async Task<bool> MarkFailedAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string error, CancellationToken ct = default)
    {
        var updated = await Set
            .Where(e => e.PythonEnvironmentId == id
                && e.Status == PythonEnvironment.StatusBuilding
                && e.Requirements == claimedRequirements
                && e.PythonVersion == claimedPythonVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, PythonEnvironment.StatusFailed)
                .SetProperty(e => e.LastError, error)
                .SetProperty(e => e.UpdatedAt, DateTime.UtcNow), ct);
        return updated > 0;
    }
}
