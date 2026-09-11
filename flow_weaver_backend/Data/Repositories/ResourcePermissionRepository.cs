using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class ResourcePermissionRepository : RepositoryBase<ResourcePermission>, IResourcePermissionRepository
{
    public ResourcePermissionRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<ResourcePermission>> ListByResourceAsync(
        string resourceType, Guid resourceId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(p => p.ResourceType == resourceType && p.ResourceId == resourceId)
            .ToListAsync(ct);

    public async Task<ResourcePermission?> FindActiveGrantAsync(
        string resourceType, Guid resourceId,
        string subjectType, Guid subjectId, string role, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(p => p.ResourceType == resourceType
                && p.ResourceId == resourceId
                && p.SubjectType == subjectType
                && p.SubjectId == subjectId
                && p.Role == role, ct);

    public async Task<IReadOnlyList<string>> GetActiveRolesForSubjectAsync(
        string resourceType, Guid resourceId, Guid subjectId, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(p => p.ResourceType == resourceType
                && p.ResourceId == resourceId
                && p.SubjectType == "user"
                && p.SubjectId == subjectId)
            .Select(p => p.Role)
            .ToListAsync(ct);

    public async Task<(ResourcePermission row, bool created)> AddGrantResolvingRaceAsync(
        ResourcePermission entry, CancellationToken ct = default)
    {
        Set.Add(entry);
        try
        {
            await Db.SaveChangesAsync(ct);
            return (entry, true);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Concurrent grants for the same (resource, subject, role) collide
            // on the partial unique index. Re-read and return the winner; the
            // caller's intent (exactly this grant exists) is satisfied either way.
            Db.Entry(entry).State = EntityState.Detached;
            var winner = await Query(activeOnly: true, tracking: false)
                .FirstOrDefaultAsync(p => p.ResourceType == entry.ResourceType
                    && p.ResourceId == entry.ResourceId
                    && p.SubjectType == entry.SubjectType
                    && p.SubjectId == entry.SubjectId
                    && p.Role == entry.Role, ct);
            if (winner is null) throw; // genuinely unexpected — surface the original
            return (winner, false);
        }
    }

    // Postgres surfaces unique-violation as Npgsql.PostgresException with
    // SqlState 23505. We probe by reflection to avoid a hard Npgsql dependency
    // at this layer — works across providers and keeps the InMemory test path
    // callable without a Postgres reference.
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            var name = inner.GetType().FullName ?? string.Empty;
            if (name.Contains("PostgresException", StringComparison.Ordinal))
            {
                var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
                if (string.Equals(sqlState, "23505", StringComparison.Ordinal)) return true;
            }
            inner = inner.InnerException;
        }
        return false;
    }
}
