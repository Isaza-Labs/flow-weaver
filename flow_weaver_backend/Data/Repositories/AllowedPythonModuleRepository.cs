using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AllowedPythonModuleRepository
    : RepositoryBase<AllowedPythonModule>, IAllowedPythonModuleRepository
{
    public AllowedPythonModuleRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<AllowedPythonModule?> FindByImportNameAsync(
        string importName, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(m => m.ImportName == importName, ct);

    public async Task<IReadOnlyList<string>> ListReadyImportNamesAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(m => m.Status == AllowedPythonModule.StatusReady)
            .Select(m => m.ImportName)
            .ToListAsync(ct);

    public async Task<AllowedPythonModule?> ClaimNextForProvisionAsync(
        DateTime staleBefore, CancellationToken ct = default)
    {
        // Candidate ids: pending pip rows, or installing rows whose claim
        // went stale. Oldest first.
        var candidates = await Set.AsNoTracking()
            .Where(m => m.IsActive
                && m.Source == AllowedPythonModule.SourcePip
                && (m.Status == AllowedPythonModule.StatusPending
                    || (m.Status == AllowedPythonModule.StatusInstalling && m.UpdatedAt < staleBefore)))
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.AllowedPythonModuleId)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            var now = DateTime.UtcNow;
            // Conditional UPDATE: only the process that flips the row from a
            // claimable state to `installing` wins. Others get 0 rows affected.
            var claimed = await Set
                .Where(m => m.AllowedPythonModuleId == id
                    && (m.Status == AllowedPythonModule.StatusPending
                        || (m.Status == AllowedPythonModule.StatusInstalling && m.UpdatedAt < staleBefore)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, AllowedPythonModule.StatusInstalling)
                    .SetProperty(m => m.UpdatedAt, now), ct);
            if (claimed == 1)
                return await Set.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.AllowedPythonModuleId == id, ct);
        }
        return null;
    }

    public async Task MarkReadyAsync(Guid id, string? installedVersion, CancellationToken ct = default)
        => await Set
            .Where(m => m.AllowedPythonModuleId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, AllowedPythonModule.StatusReady)
                .SetProperty(m => m.InstalledVersion, installedVersion)
                .SetProperty(m => m.Error, (string?)null)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

    public async Task<bool> RenameImportNameAsync(
        Guid id, string importName, CancellationToken ct = default)
    {
        // Import names are case-sensitive in Python, so the uniqueness check is too —
        // matching FindByImportNameAsync above.
        var taken = await Set.AnyAsync(
            m => m.ImportName == importName && m.AllowedPythonModuleId != id, ct);
        if (taken) return false;

        var updated = await Set
            .Where(m => m.AllowedPythonModuleId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ImportName, importName)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);
        return updated > 0;
    }

    public async Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default)
        => await Set
            .Where(m => m.AllowedPythonModuleId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, AllowedPythonModule.StatusFailed)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);
}
