using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// MCP server reads. Generic CRUD resolves via the open-generic
// IRepository<McpServer>; this adds the by-id worker lookup and the
// active-list projection the management UI / agent tools need. Mirrors
// IIntegrationRepository.
public interface IMcpServerRepository : IRepository<McpServer>
{
    // By-id lookup, active only — the worker / agent dispatch path resolves
    // a server straight from a (validated) id.
    Task<McpServer?> FindActiveByIdAsync(Guid mcpServerId, CancellationToken ct = default);

    // Active server with this exact (case-insensitive) name — the portable
    // identity a workflow bundle's `server` key resolves against. Exact,
    // never fuzzy: a near miss binds a step to the wrong system.
    Task<McpServer?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // Active servers (optionally only Enabled ones), newest first.
    Task<IReadOnlyList<McpServer>> ListActiveAsync(
        bool enabledOnly, CancellationToken ct = default);
}
