using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Write-only auth material accepted on create/update. Maps to McpAuthConfig and
// is encrypted at the service layer; never echoed back (the response exposes
// has_* booleans instead).
public class McpAuthInput
{
    [JsonPropertyName("api_key_header")] public string? ApiKeyHeader { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    // basic auth — username is not secret, password is.
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("secret_headers")] public Dictionary<string, string>? SecretHeaders { get; set; }

    // OAuth 2.1 (F2) — client + endpoints. Tokens are server-managed, never accepted here.
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
    [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    [JsonPropertyName("redirect_uri")] public string? RedirectUri { get; set; }
}

public class CreateMcpServerRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    [JsonPropertyName("auth")] public McpAuthInput? Auth { get; set; }
    [JsonPropertyName("headers")] public JsonElement? Headers { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

// Patch semantics — only non-null fields are applied. `auth` is only replaced
// when supplied (so editing a server doesn't require re-entering secrets).
public class UpdateMcpServerRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    [JsonPropertyName("auth")] public McpAuthInput? Auth { get; set; }
    [JsonPropertyName("headers")] public JsonElement? Headers { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool? TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class McpServerResponse
{
    [JsonPropertyName("mcp_server_id")] public Guid McpServerId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("transport")] public string Transport { get; set; } = "http";
    [JsonPropertyName("auth_type")] public string AuthType { get; set; } = "none";

    // True when any auth material is stored (never the material itself).
    [JsonPropertyName("has_auth")] public bool HasAuth { get; set; }
    [JsonPropertyName("has_api_key")] public bool HasApiKey { get; set; }
    [JsonPropertyName("has_token")] public bool HasToken { get; set; }
    // basic auth: the non-secret username is echoed; the password is not.
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("has_password")] public bool HasPassword { get; set; }
    [JsonPropertyName("has_client_secret")] public bool HasClientSecret { get; set; }
    // OAuth (F2): whether a usable access token is currently stored.
    [JsonPropertyName("has_access_token")] public bool HasAccessToken { get; set; }

    [JsonPropertyName("headers")] public JsonElement Headers { get; set; }
    [JsonPropertyName("tls_skip_verify")] public bool TlsSkipVerify { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    // `protocol_version` is deliberately absent: McpServer.ProtocolVersion has
    // never been written (the SDK's negotiated version is not surfaced by
    // IMcpClient) — returning it was a permanent null. Plumb it through the
    // connection factory before re-adding.
    [JsonPropertyName("last_tools_synced_at")] public DateTime? LastToolsSyncedAt { get; set; }
    [JsonPropertyName("last_checked_at")] public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("tool_count")] public int ToolCount { get; set; }

    // Non-secret OAuth config, echoed so the edit form can show/keep it.
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
    [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    [JsonPropertyName("redirect_uri")] public string? RedirectUri { get; set; }
}

// Result of POST {id}/oauth/start — the URL the browser must visit to consent.
public class McpOAuthStartResponse
{
    [JsonPropertyName("authorization_url")] public string AuthorizationUrl { get; set; } = string.Empty;
}

public class McpToolResponse
{
    [JsonPropertyName("mcp_tool_id")] public Guid McpToolId { get; set; }
    [JsonPropertyName("mcp_server_id")] public Guid McpServerId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

// Result of a "test & sync tools" action: the resolved status + how many tools
// were upserted, or the error that set status to unreachable/needs_authorization.
public class McpSyncResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("tools_synced")] public int ToolsSynced { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
