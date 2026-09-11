using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Services.Mcp;

public sealed class McpConnectionFactory : IMcpConnectionFactory
{
    private readonly ICredentialEncryptionService _crypto;
    private readonly IUrlGuard _urlGuard;
    private readonly IMcpTokenService _tokens;

    public McpConnectionFactory(ICredentialEncryptionService crypto, IUrlGuard urlGuard, IMcpTokenService tokens)
    {
        _crypto = crypto;
        _urlGuard = urlGuard;
        _tokens = tokens;
    }

    public async Task<McpConnection> CreateAsync(McpServer server, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"MCP server URL is not a valid absolute http(s) URL: '{server.Url}'.");
        }

        // SSRF guard — throws InvalidOperationException on a blocked target.
        _urlGuard.EnsureSafe(server.Url, allowPrivate: server.AllowPrivateNetwork);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Static, non-secret custom headers.
        if (server.Headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in server.Headers.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    headers[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }

        // Secret auth material.
        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _crypto);
        switch (server.AuthType?.ToLowerInvariant())
        {
            case "api_key":
                if (!string.IsNullOrEmpty(auth.ApiKey))
                {
                    var header = string.IsNullOrWhiteSpace(auth.ApiKeyHeader) ? "X-API-Key" : auth.ApiKeyHeader!;
                    headers[header] = auth.ApiKey!;
                }
                break;

            case "bearer":
                if (!string.IsNullOrEmpty(auth.Token))
                    headers["Authorization"] = "Bearer " + auth.Token;
                break;

            case "basic":
                if (!string.IsNullOrEmpty(auth.Username) || !string.IsNullOrEmpty(auth.Password))
                {
                    var raw = $"{auth.Username}:{auth.Password}";
                    headers["Authorization"] =
                        "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
                }
                break;

            case "headers":
                if (auth.SecretHeaders is not null)
                {
                    foreach (var kv in auth.SecretHeaders)
                        headers[kv.Key] = kv.Value;
                }
                break;

            case "oauth_client_credentials":
            case "oauth_authorization_code":
                // Resolve/refresh a valid access token (client-credentials
                // re-grants; authorization-code refreshes) and set the bearer.
                var token = await _tokens.GetValidAccessTokenAsync(server, ct);
                if (!string.IsNullOrEmpty(token))
                    headers["Authorization"] = "Bearer " + token;
                break;
        }

        return new McpConnection(endpoint, headers, server.TLSSkipVerify);
    }
}
