using System.Text.Json;
using flow_weaver_backend.Services.Mcp;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Virtual workflow node: calls a tool on a registered MCP server via the shared
// McpToolExecutor (same path as the agent's call_mcp_tool). Type "mcp_call"; the
// seeded snippet of that type carries the node's config in config_overrides
// { mcp_server_id, tool, arguments } (`tool_name` is the accepted alias),
// already template-resolved by the
// engine into request.InputPayload. RequiresCompensation — the external effect
// is unknown, like integration_action.
public sealed class McpCallHandler : ISnippetHandler
{
    public string Type => "mcp_call";
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly IMcpToolExecutor _executor;
    private readonly ILogger<McpCallHandler> _logger;

    public McpCallHandler(IMcpToolExecutor executor, ILogger<McpCallHandler> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // `tool` is the CANONICAL spelling; `tool_name` is this product's older
        // one and is normalized into it (snippets/SPEC.md `mcp_call`). A node
        // carrying both means `tool`.
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);

        if (!TryReadGuid(input, "mcp_server_id", out var serverId, out var idErr))
            return Fail(idErr);

        var toolName = ReadString(input, "tool");
        if (string.IsNullOrWhiteSpace(toolName))
            return Fail(
                "input.tool is required for mcp_call nodes (the tool's name on the MCP server; "
                + "`tool_name` is accepted as an alias for it).");

        var arguments = input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : JsonDocument.Parse("{}").RootElement;

        var result = await _executor.ExecuteAsync(serverId, toolName!, arguments, ct);

        if (result.IsError)
        {
            _logger.LogWarning(
                "mcp_call node {Node} failed: server={Server} tool={Tool}", request.NodeId, serverId, toolName);
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            content = result.Content,
            structured = result.Structured,
            is_error = result.IsError,
        });

        return new SnippetResult
        {
            Success = !result.IsError,
            Output = output,
            // The tool belongs to the MCP server, not to this handler: `search_docs` and
            // `create_ticket` arrive through the same call and look identical from here.
            // The author of the snippet knows which one they wired up; this handler does
            // not, and guessing would be the old inference wearing a new name.
            Change = StepChange.AuthorDecides,
            Logs = $"mcp_call {toolName} on server {serverId} → {(result.IsError ? "error" : "ok")}",
            Error = result.IsError ? Truncate(result.Content, 500) : string.Empty,
        };
    }

    private static bool TryReadGuid(JsonElement input, string key, out Guid value, out string error)
    {
        value = Guid.Empty;
        error = string.Empty;
        if (!input.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            error = $"input.{key} is required for mcp_call nodes. Set it in the node's config_overrides.";
            return false;
        }
        if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out value))
        {
            error = $"input.{key} must be a GUID. Use list_mcp_servers / discover_mcp_tools to resolve the real id.";
            return false;
        }
        return true;
    }

    private static string? ReadString(JsonElement input, string key)
        => input.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static SnippetResult Fail(string error) => new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
