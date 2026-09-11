using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class McpServerRepository : RepositoryBase<McpServer>, IMcpServerRepository
{
    public McpServerRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<McpServer?> FindActiveByIdAsync(Guid mcpServerId, CancellationToken ct = default)
        => await Set.AsNoTracking()
            .FirstOrDefaultAsync(s => s.McpServerId == mcpServerId && s.IsActive, ct);

    public async Task<McpServer?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(s => s.Name.ToLower() == needle, ct);
    }

    public async Task<IReadOnlyList<McpServer>> ListActiveAsync(
        bool enabledOnly, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);
        if (enabledOnly) q = q.Where(s => s.Enabled);
        return await q.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
    }
}
