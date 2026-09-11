using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Searches the cached MCP tools (McpTool rows) across the enabled
// servers. Read-only (cap mcp.read, autonomous).
public sealed class DiscoverMcpToolsHandler : IToolHandler
{
    private const int DefaultLimit = 25;

    public string Name => "discover_mcp_tools";
    public string Description =>
        "Tier: autonomous. Searches the cached MCP tools across the enabled " +
        "servers. Filter by `keyword` (matches tool name/description), " +
        "`server_name`, or `mcp_server_id`. Returns each tool's name, server, " +
        "description and input JSON schema — pass the exact `tool_name` + " +
        "`mcp_server_id` to `call_mcp_tool`.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "keyword":{"type":"string","description":"Filter by substring of the tool name or description."},
          "server_name":{"type":"string","description":"Only tools from the server with this name."},
          "mcp_server_id":{"type":"string","description":"Only tools from this server id."},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}
        },"additionalProperties":false}
        """).RootElement;

    private readonly IMcpServerRepository _servers;
    private readonly IMcpToolRepository _tools;
    private readonly ICurrentUser _caller;

    public DiscoverMcpToolsHandler(IMcpServerRepository servers, IMcpToolRepository tools, ICurrentUser caller)
    {
        _servers = servers;
        _tools = tools;
        _caller = caller;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var keyword = GetString(args, "keyword")?.Trim().ToLowerInvariant();
        var serverName = GetString(args, "server_name")?.Trim();
        var serverIdRaw = GetString(args, "mcp_server_id");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 100) : DefaultLimit;

        // Only surface tools from enabled servers.
        var servers = await _servers.ListActiveAsync(enabledOnly: true, ct);
        var serverNameById = servers.ToDictionary(s => s.McpServerId, s => s.Name);

        Guid? filterServerId = Guid.TryParse(serverIdRaw, out var sid) ? sid : null;
        if (filterServerId is null && !string.IsNullOrEmpty(serverName))
        {
            filterServerId = servers
                .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase))
                ?.McpServerId;
        }

        var tools = await _tools.ListActiveByCompanyAsync(ct);
        IEnumerable<McpTool> matches = tools.Where(t => serverNameById.ContainsKey(t.McpServerId));
        if (filterServerId is Guid fsid) matches = matches.Where(t => t.McpServerId == fsid);
        if (!string.IsNullOrEmpty(keyword))
        {
            matches = matches.Where(t =>
                t.Name.ToLowerInvariant().Contains(keyword)
                || (t.Description?.ToLowerInvariant().Contains(keyword) ?? false));
        }

        var page = matches.Take(limit).Select(t => new
        {
            tool_name = t.Name,
            mcp_server_id = t.McpServerId,
            server_name = serverNameById.GetValueOrDefault(t.McpServerId),
            title = t.Title,
            description = t.Description,
            input_schema = t.InputSchema.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : t.InputSchema,
        }).ToList();

        return JsonSerializer.SerializeToElement(new { count = page.Count, tools = page });
    }

    private static string? GetString(JsonElement args, string key)
        => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
