using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Mcp;

// Builds an McpConnection from an McpServer row: validates the URL against the
// SSRF guard, decrypts the auth material, and turns it into request headers.
public interface IMcpConnectionFactory
{
    Task<McpConnection> CreateAsync(McpServer server, CancellationToken ct = default);
}
