using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class AiApiSpecRepository : RepositoryBase<AiApiSpec>, IAiApiSpecRepository
{
    public AiApiSpecRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Dictionary<string, AiApiSpec>> GetByApisAsync(
        IReadOnlyCollection<string> apis, CancellationToken ct = default)
    {
        if (apis.Count == 0) return new Dictionary<string, AiApiSpec>();
        return await Query(activeOnly: false, tracking: true)
            .Where(s => apis.Contains(s.Api))
            .ToDictionaryAsync(s => s.Api, ct);
    }

    public async Task<IReadOnlyList<AiApiSpec>> ListOrderedAsync(
        int limit, int offset, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .OrderBy(s => s.Api)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

    public async Task<AiApiSpec?> FindByApiAsync(string api, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: true)
            .FirstOrDefaultAsync(s => s.Api == api, ct);

    public async Task<bool> ApiExistsForOtherAsync(
        string api, Guid excludeId, CancellationToken ct = default)
        => await Query(activeOnly: false, tracking: false)
            .AnyAsync(s => s.Api == api && s.AiApiSpecId != excludeId, ct);

    public async Task<IReadOnlyList<ApiSpecContent>> ListActiveApiContentsAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Select(s => new ApiSpecContent(s.Api, s.Content))
            .ToListAsync(ct);

    public async Task<AiApiSpec?> FindActiveByApiAsync(string api, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(s => s.Api == api, ct);

    public async Task<string?> GetActiveContentByApiAsync(string api, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(s => s.Api == api)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);

    public async Task<Dictionary<string, string>> GetActiveContentsByApisAsync(
        IReadOnlyCollection<string> apis, CancellationToken ct = default)
    {
        if (apis.Count == 0) return new Dictionary<string, string>();
        return await Query(activeOnly: true, tracking: false)
            .Where(s => apis.Contains(s.Api))
            .Select(s => new { s.Api, s.Content })
            .ToDictionaryAsync(x => x.Api, x => x.Content, ct);
    }
}
