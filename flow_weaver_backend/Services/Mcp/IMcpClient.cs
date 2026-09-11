using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Mcp;

// Thin wrapper over the official ModelContextProtocol SDK client. Opens a fresh
// session per operation (connect → initialize → op → close), shared by the agent
// tools and the workflow node handler. Throws on connect/list failures (so the
// management sync/health can surface Status); CallToolAsync surfaces tool-side
// errors via McpCallResult.IsError but throws on transport failures.
public interface IMcpClient
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct = default);

    Task<McpCallResult> CallToolAsync(
        McpServer server, string toolName, JsonElement arguments, CancellationToken ct = default);
}
