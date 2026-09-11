using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Dtos;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

// Reads the integration's AuthConfig jsonb + its custom Headers jsonb and
// applies them to an outgoing HttpRequestMessage. Stateless, so it is
// registered as a singleton — a single instance is reused across requests.
//
// Auth methods recognized (enum-less so specs can add new values without
// touching this code):
//   token   : Authorization: {Prefix} {Token}      (Prefix default "Token")
//   basic   : Authorization: Basic base64(user:pwd)
//   bearer  : Authorization: Bearer {Token}
//   oauth2  : Authorization: Bearer {Token}        (static, pre-issued token)
//   api_key : {Header}: {Token}                    (Header default "X-API-Key")
//
// oauth2_client_credentials is deliberately NOT applied here: obtaining the
// access token is an async HTTP call (token endpoint + cache), which this
// synchronous builder cannot make. Callers that support it go through
// IIntegrationAuthApplier.ApplyAsync, which layers the Bearer token from
// IntegrationOAuthTokenService on top of this builder's custom headers.
//
// If Method is empty, auto-detect: "token" when Token is set,
// "basic" when Username+Password are set.
public class IntegrationAuthBuilder
{
    private readonly ILogger<IntegrationAuthBuilder> _logger;

    public IntegrationAuthBuilder(ILogger<IntegrationAuthBuilder> logger)
    {
        _logger = logger;
    }

    public void Apply(HttpRequestMessage request, IntegrationModel integration)
    {
        ApplyCustomHeaders(request, integration.Headers);
        ApplyAuth(request, integration.AuthConfig, integration.IntegrationId);
    }

    // Parses the AuthConfig jsonb without applying it — the async applier and
    // the OAuth token service need the method + client credentials. Returns
    // null when the config is absent or malformed.
    internal static IntegrationAuthConfig? TryParse(JsonElement authConfig)
    {
        if (authConfig.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return JsonSerializer.Deserialize<IntegrationAuthConfig>(authConfig.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ApplyCustomHeaders(HttpRequestMessage request, JsonElement headers)
    {
        if (headers.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in headers.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var value = prop.Value.GetString();
            if (string.IsNullOrEmpty(value)) continue;

            // Don't let a custom header accidentally override Authorization —
            // AuthConfig is the authoritative source for that one.
            if (prop.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            request.Headers.TryAddWithoutValidation(prop.Name, value);
        }
    }

    private void ApplyAuth(HttpRequestMessage request, JsonElement authConfig, Guid integrationId)
    {
        if (authConfig.ValueKind != JsonValueKind.Object) return;

        IntegrationAuthConfig? auth;
        try
        {
            auth = JsonSerializer.Deserialize<IntegrationAuthConfig>(authConfig.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "integration.auth.parse_failed integration_id={IntegrationId}", integrationId);
            throw;
        }
        if (auth is null) return;

        var method = string.IsNullOrWhiteSpace(auth.Method)
            ? AutoDetect(auth)
            : auth.Method.ToLowerInvariant();

        // Never log token/password values — only the chosen method.
        _logger.LogDebug(
            "integration.auth.apply integration_id={IntegrationId} auth_method={AuthMethod}",
            integrationId, string.IsNullOrEmpty(method) ? "none" : method);

        switch (method)
        {
            case "token":
                var prefix = string.IsNullOrWhiteSpace(auth.Prefix) ? "Token" : auth.Prefix;
                if (!string.IsNullOrEmpty(auth.Token))
                    request.Headers.Authorization = new AuthenticationHeaderValue(prefix, auth.Token);
                break;

            case "basic":
                if (!string.IsNullOrEmpty(auth.Username))
                {
                    var raw = $"{auth.Username}:{auth.Password}";
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                }
                break;

            case "bearer":
            case "oauth2":
                if (!string.IsNullOrEmpty(auth.Token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
                break;

            case "api_key":
                var header = string.IsNullOrWhiteSpace(auth.Header) ? "X-API-Key" : auth.Header;
                if (!string.IsNullOrEmpty(auth.Token))
                    request.Headers.TryAddWithoutValidation(header, auth.Token);
                break;

            case IntegrationAuthConfig.OAuth2ClientCredentials:
                // Applied asynchronously by IIntegrationAuthApplier (token
                // fetch + cache); nothing to do in the sync builder.
                break;

            // Unknown / empty method = no auth applied. The upstream decides
            // whether to respond 401, which surfaces cleanly in the health check.
        }
    }

    private static string AutoDetect(IntegrationAuthConfig auth)
    {
        if (!string.IsNullOrEmpty(auth.Token)) return "token";
        if (!string.IsNullOrEmpty(auth.Username)) return "basic";
        return string.Empty;
    }
}
