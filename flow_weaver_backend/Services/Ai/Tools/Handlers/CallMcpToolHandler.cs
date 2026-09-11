using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Invokes one tool on a registered MCP server via the shared McpToolExecutor.
// Cap mcp.execute, tier single_confirm. Enforces the per-server / per-tool
// grant conditions itself (the dispatcher's coarse gate only checks the cap is
// held in SOME context) — but only in granular RBAC mode; legacy mode relies on
// the dispatcher role gate + the unconditioned built-in operator grant.
public sealed class CallMcpToolHandler : IToolHandler
{
    public string Name => "call_mcp_tool";
    public string Description =>
        "Tier: single_confirm. Calls a tool on a registered MCP server. Resolve " +
        "the server + tool first with `list_mcp_servers` / `discover_mcp_tools`, " +
        "then pass `mcp_server_id` (or `server_name`), the exact `tool_name`, and " +
        "an `arguments` object matching the tool's input schema. Returns the " +
        "tool's `content`, optional `structured` payload, and an `is_error` flag. " +
        "Mutating calls belong inside the single batch Plan block.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "mcp_server_id":{"type":"string","description":"The server id (from discover_mcp_tools). Preferred over server_name."},
          "server_name":{"type":"string","description":"The server name, if the id isn't known."},
          "tool_name":{"type":"string","description":"The exact tool name on the server."},
          "arguments":{"type":"object","description":"Arguments matching the tool's input schema.","additionalProperties":true,"default":{}}
        },"required":["tool_name"],"additionalProperties":false}
        """).RootElement;

    private readonly IMcpServerRepository _servers;
    private readonly IMcpToolExecutor _executor;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _settings;
    private readonly ICurrentUser _caller;
    private readonly ILogger<CallMcpToolHandler> _logger;

    public CallMcpToolHandler(
        IMcpServerRepository servers,
        IMcpToolExecutor executor,
        IEffectivePermissions effective,
        IAppSettingsService settings,
        ICurrentUser caller,
        ILogger<CallMcpToolHandler> logger)
    {
        _servers = servers;
        _executor = executor;
        _effective = effective;
        _settings = settings;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var toolName = GetString(args, "tool_name")?.Trim();
        if (string.IsNullOrEmpty(toolName))
            return JsonSerializer.SerializeToElement(new { error = "tool_name is required", is_error = true });


        McpServer? server = null;
        if (Guid.TryParse(GetString(args, "mcp_server_id"), out var sid))
        {
            server = await _servers.GetByIdAsync(sid, activeOnly: true, tracking: false, ct);
        }
        else if (GetString(args, "server_name") is { Length: > 0 } serverName)
        {
            var all = await _servers.ListActiveAsync(enabledOnly: true, ct);
            server = all.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        }

        if (server is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                error = "MCP server not found. Use list_mcp_servers to resolve it.",
                is_error = true,
            });
        }

        // Per-server / per-tool authorization (granular mode only).
        var settings = await _settings.GetAsync(ct);
        if (RbacModes.IsGranular(settings.RbacMode))
        {
            var permCtx = new PermissionContext(McpServerId: server.McpServerId, McpToolName: toolName);
            if (!await _effective.HasAsync("mcp.execute", permCtx, ct))
            {
                _logger.LogWarning(
                    "ai.tool.call_mcp_tool.denied server={Server} tool={Tool}", server.McpServerId, toolName);
                return JsonSerializer.SerializeToElement(new
                {
                    error = $"permission denied: you cannot call tool '{toolName}' on server '{server.Name}'.",
                    is_error = true,
                });
            }
        }

        var arguments = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : JsonDocument.Parse("{}").RootElement;

        var result = await _executor.ExecuteAsync(server.McpServerId, toolName, arguments, ct);
        return JsonSerializer.SerializeToElement(new
        {
            content = result.Content,
            structured = result.Structured,
            is_error = result.IsError,
        });
    }

    private static string? GetString(JsonElement args, string key)
        => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
