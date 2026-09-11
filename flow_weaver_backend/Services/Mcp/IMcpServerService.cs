using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Mcp;

// CRUD for MCP servers + tool discovery. Returns ActionResults directly
// (the controller is a thin passthrough), mirroring IntegrationService.
public interface IMcpServerService
{
    Task<ActionResult<ListResponse<McpServerResponse>>> ListAsync(int limit, int offset, CancellationToken ct = default);
    Task<ActionResult<McpServerResponse>> GetAsync(Guid id, CancellationToken ct = default);
    Task<ActionResult<McpServerResponse>> CreateAsync(CreateMcpServerRequest dto, CancellationToken ct = default);
    Task<ActionResult<McpServerResponse>> UpdateAsync(Guid id, UpdateMcpServerRequest dto, CancellationToken ct = default);
    Task<ActionResult<McpServerResponse>> DeleteAsync(Guid id, CancellationToken ct = default);

    // Tools of one server (from the cached McpTool rows).
    Task<ActionResult<ListResponse<McpToolResponse>>> ListToolsAsync(
        Guid mcpServerId, int limit, int offset, CancellationToken ct = default);

    // Every cached tool (builder palette / agent discovery).
    Task<ActionResult<ListResponse<McpToolResponse>>> ListAllToolsAsync(CancellationToken ct = default);

    // Connect (initialize + tools/list), upsert the tool cache, and set Status.
    Task<ActionResult<McpSyncResult>> TestAndSyncAsync(Guid id, CancellationToken ct = default);
}
