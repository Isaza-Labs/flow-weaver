using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class SecretRepository : RepositoryBase<Secret>, ISecretRepository
{
    public SecretRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Secret?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        return isGuid
            ? await q.FirstOrDefaultAsync(s => s.SecretId == id, ct)
            : await q.FirstOrDefaultAsync(s => s.Name == name, ct);
    }
}
