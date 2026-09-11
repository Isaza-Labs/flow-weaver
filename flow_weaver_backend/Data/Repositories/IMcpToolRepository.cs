using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// MCP tool reads. Generic CRUD resolves via the open-generic IRepository<McpTool>;
// this adds the by-parent list used by the tools/list upsert and the agent's
// discover_mcp_tools search. Mirrors IIntegrationActionRepository.
public interface IMcpToolRepository : IRepository<McpTool>
{
    // Cached tools of one server (used by the sync upsert).
    Task<IReadOnlyList<McpTool>> ListByServerAsync(
        Guid mcpServerId, bool activeOnly = true, CancellationToken ct = default);

    // All active cached tools (the agent's discover search filters these
    // in memory by keyword / server).
    Task<IReadOnlyList<McpTool>> ListActiveByCompanyAsync(CancellationToken ct = default);
}
