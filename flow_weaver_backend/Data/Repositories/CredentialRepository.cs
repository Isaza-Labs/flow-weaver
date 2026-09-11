using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class CredentialRepository : RepositoryBase<Credential>, ICredentialRepository
{
    public CredentialRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Credential?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        return isGuid
            ? await q.FirstOrDefaultAsync(c => c.CredentialId == id, ct)
            : await q.FirstOrDefaultAsync(c => c.Name == name, ct);
    }

    public async Task<Credential?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(c => c.Name.ToLower() == needle, ct);
    }

    public async Task<IReadOnlyList<CredentialListRow>> ListMetadataAsync(
        string? type, string? authMethod, string? nameContains,
        int limit, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);

        if (!string.IsNullOrEmpty(type))
            q = q.Where(c => c.Type == type);
        if (authMethod is "password" or "key")
            q = q.Where(c => c.AuthMethod == authMethod);
        if (!string.IsNullOrEmpty(nameContains))
        {
            // EF.Functions.ILike is the fastest case-insensitive contains
            // on Postgres; falls back to ToLower comparison for in-memory
            // tests.
            var like = $"%{nameContains}%";
            q = q.Where(c => EF.Functions.ILike(c.Name, like));
        }

        return await q
            .OrderBy(c => c.Name)
            .Take(limit)
            .Select(c => new CredentialListRow(
                c.CredentialId,
                c.Name,
                c.Type,
                c.Username,
                c.AuthMethod,
                c.EncryptedPrivateKey != null,
                c.CreatedAt,
                c.UpdatedAt))
            .ToListAsync(ct);
    }
}
