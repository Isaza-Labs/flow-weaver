using System.Text.Json;

namespace flow_weaver_backend.Services.Mcp;

// The single point that executes an MCP tools/call, shared by the chat agent
// (call_mcp_tool) and the workflow node (mcp_call). Loads the server, delegates
// to IMcpClient, and returns a normalized result — transport / not-found /
// timeout failures are surfaced as McpCallResult.IsError rather than thrown,
// so both callers can render them uniformly.
public interface IMcpToolExecutor
{
    Task<McpCallResult> ExecuteAsync(
        Guid mcpServerId, string toolName, JsonElement arguments, CancellationToken ct = default);
}
