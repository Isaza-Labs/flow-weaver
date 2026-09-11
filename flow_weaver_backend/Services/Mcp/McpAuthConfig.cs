using System.Text.Json;

namespace flow_weaver_backend.Services.Mcp;

// Decrypted auth material for an McpServer. Serialized to a JSON document and
// encrypted at rest in McpServer.AuthConfigEncrypted (ICredentialEncryptionService).
// NEVER returned to clients — the API surfaces has_* booleans instead.
//
// Which fields are populated depends on McpServer.AuthType:
//   api_key                    → ApiKeyHeader + ApiKey
//   bearer                     → Token
//   basic                      → Username + Password
//   (custom secret headers)    → SecretHeaders
//   oauth_client_credentials   → ClientId/ClientSecret/TokenEndpoint/Scopes (+ tokens)
//   oauth_authorization_code   → the above + AuthorizationEndpoint/RedirectUri (+ refresh)
public sealed class McpAuthConfig
{
    // ── api_key ──
    public string? ApiKeyHeader { get; set; }
    public string? ApiKey { get; set; }

    // ── bearer ──
    public string? Token { get; set; }

    // ── basic (username / password) ──
    public string? Username { get; set; }
    public string? Password { get; set; }

    // ── arbitrary secret headers ──
    public Dictionary<string, string>? SecretHeaders { get; set; }

    // ── OAuth 2.1 (F2) — client + endpoints ──
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public List<string>? Scopes { get; set; }
    public string? RedirectUri { get; set; }

    // ── OAuth 2.1 (F2) — obtained tokens ──
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }

    // Transient PKCE verifier + CSRF nonce held between oauth/start and
    // oauth/callback (cleared once tokens are obtained).
    public string? CodeVerifier { get; set; }
    public string? StateNonce { get; set; }
}

// Round-trips an McpAuthConfig through ICredentialEncryptionService. The service
// layer encrypts on write; the connection factory decrypts on use.
public static class McpAuthConfigCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static McpAuthConfig Decrypt(byte[]? cipher, ICredentialEncryptionService crypto)
    {
        var json = crypto.Decrypt(cipher);
        if (string.IsNullOrEmpty(json)) return new McpAuthConfig();
        try
        {
            return JsonSerializer.Deserialize<McpAuthConfig>(json, Options) ?? new McpAuthConfig();
        }
        catch (JsonException)
        {
            return new McpAuthConfig();
        }
    }

    public static byte[]? Encrypt(McpAuthConfig config, ICredentialEncryptionService crypto)
        => crypto.Encrypt(JsonSerializer.Serialize(config, Options));
}
