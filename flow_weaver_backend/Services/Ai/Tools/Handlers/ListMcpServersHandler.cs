using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Lists the enabled MCP servers so the agent can pick one before
// discovering/calling its tools. Read-only (cap mcp.read, autonomous).
public sealed class ListMcpServersHandler : IToolHandler
{
    public string Name => "list_mcp_servers";
    public string Description =>
        "Tier: autonomous. Lists the registered MCP servers " +
        "(mcp_server_id, name, status, tool_count). Use this first to find a " +
        "server, then `discover_mcp_tools` to browse its tools and " +
        "`call_mcp_tool` to invoke one.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{},"additionalProperties":false}
        """).RootElement;

    private readonly IMcpServerRepository _servers;
    private readonly IMcpToolRepository _tools;
    private readonly ICurrentUser _caller;

    public ListMcpServersHandler(IMcpServerRepository servers, IMcpToolRepository tools, ICurrentUser caller)
    {
        _servers = servers;
        _tools = tools;
        _caller = caller;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var servers = await _servers.ListActiveAsync(enabledOnly: true, ct);
        var allTools = await _tools.ListActiveByCompanyAsync(ct);
        var counts = allTools.GroupBy(t => t.McpServerId).ToDictionary(g => g.Key, g => g.Count());

        var result = servers.Select(s => new
        {
            mcp_server_id = s.McpServerId,
            name = s.Name,
            status = s.Status,
            tool_count = counts.GetValueOrDefault(s.McpServerId),
        }).ToList();

        return JsonSerializer.SerializeToElement(new { count = result.Count, servers = result });
    }
}
