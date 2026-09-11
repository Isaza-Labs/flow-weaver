using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Parsed form of the integration.AuthConfig jsonb column. CONTAINS SECRETS
// (Token, Password). `internal` because this type is only for the HTTP
// request builder inside the integration service — NEVER return it in an
// API response and NEVER log full instances.
//
// Auth methods recognized:
//   token   : Authorization: {Prefix} {Token}           (Prefix defaults to "Token")
//   basic   : Basic base64(Username:Password)
//   bearer  : Authorization: Bearer {Token}
//   oauth2  : Authorization: Bearer {Token}             (static, pre-issued token)
//   api_key : {Header}: {Token}                         (Header defaults to "X-API-Key")
//   oauth2_client_credentials :
//             Authorization: Bearer {access_token obtained at call time from
//             TokenUrl via the client_credentials grant}. The token itself is
//             cached in memory (IntegrationOAuthTokenService), never stored in
//             this config — only the client credentials live here. Mirrors the
//             MCP servers' oauth_client_credentials support.
//
// If Method is empty, the service auto-detects: "token" when Token is set,
// "basic" when Username+Password are set.
internal class IntegrationAuthConfig
{
    // Method value for the client-credentials OAuth grant. Referenced from
    // the auth builder, the async applier, and the token service — keep the
    // literal in one place.
    public const string OAuth2ClientCredentials = "oauth2_client_credentials";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    // Custom header name for api_key method.
    [JsonPropertyName("header")]
    public string Header { get; set; } = string.Empty;

    // Prefix before the Token in the Authorization header (e.g., "Token", "Bearer").
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = string.Empty;

    // ── oauth2_client_credentials ──
    // The authorization server's token endpoint (absolute URL). Goes through
    // IUrlGuard with the integration's AllowPrivateNetwork flag, like every
    // other outbound integration URL.
    [JsonPropertyName("token_url")]
    public string TokenUrl { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; } = string.Empty;

    // Space-separated scope string, sent verbatim as `scope`. Optional.
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    // Optional `audience` parameter (Auth0-style authorization servers).
    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    public override string ToString() => $"IntegrationAuthConfig(Method={Method})";
}
