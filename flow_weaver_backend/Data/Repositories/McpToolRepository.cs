using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class McpToolRepository : RepositoryBase<McpTool>, IMcpToolRepository
{
    public McpToolRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<McpTool>> ListByServerAsync(
        Guid mcpServerId, bool activeOnly = true, CancellationToken ct = default)
        => await Query(activeOnly, tracking: true)
            .Where(t => t.McpServerId == mcpServerId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<McpTool>> ListActiveByCompanyAsync(
        CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
}
