using System.Text.Json;

namespace flow_weaver_backend.Services.Mcp;

// A tool as reported by an MCP server's tools/list (projected from the SDK's
// McpClientTool). Cached into McpTool rows and surfaced to the agent/builder.
public sealed record McpToolDescriptor(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema);

// Normalized result of a tools/call: text content blocks concatenated, the
// optional structured payload, and the server's error flag.
public sealed record McpCallResult(
    string Content,
    JsonElement? Structured,
    bool IsError);

// Everything the client needs to open a session to one server. Built by
// IMcpConnectionFactory (decrypts auth, applies SSRF guard). Headers is a
// concrete Dictionary so it is assignable to the SDK's AdditionalHeaders.
public sealed record McpConnection(
    Uri Endpoint,
    Dictionary<string, string> Headers,
    bool TlsSkipVerify);

// Named IHttpClientFactory clients for MCP. The insecure variant accepts any
// TLS cert (TLSSkipVerify), mirroring the integration/integration-insecure pair.
public static class McpHttpClients
{
    public const string Secure = "mcp";
    public const string Insecure = "mcp-insecure";
}
