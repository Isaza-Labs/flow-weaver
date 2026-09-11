using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace flow_weaver_backend.Services.Mcp;

public sealed class McpToolExecutor : IMcpToolExecutor
{
    private readonly IMcpServerRepository _servers;
    private readonly IMcpClient _client;
    private readonly ILogger<McpToolExecutor> _logger;

    public McpToolExecutor(
        IMcpServerRepository servers,
        IMcpClient client,
        ILogger<McpToolExecutor> logger)
    {
        _servers = servers;
        _client = client;
        _logger = logger;
    }

    public async Task<McpCallResult> ExecuteAsync(
        Guid mcpServerId, string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return Error("tool_name is required.");

        var server = await _servers.GetByIdAsync(mcpServerId, activeOnly: true, tracking: false, ct);
        if (server is null)
            return Error($"MCP server '{mcpServerId}' was not found.");
        if (!server.Enabled)
            return Error($"MCP server '{server.Name}' is disabled.");

        try
        {
            return await _client.CallToolAsync(server, toolName, arguments, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — let it propagate
        }
        catch (OperationCanceledException)
        {
            return Error($"MCP call to '{toolName}' on '{server.Name}' timed out.");
        }
        catch (InvalidOperationException ex)
        {
            // SSRF guard / bad URL.
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP call failed: server {Server} tool {Tool}", server.McpServerId, toolName);
            return Error($"MCP call to '{toolName}' on '{server.Name}' failed: {ex.Message}");
        }
    }

    private static McpCallResult Error(string message) => new(message, Structured: null, IsError: true);
}
