using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;

namespace flow_weaver_backend.Services.InventorySource;

// The `config` of an inventory source whose type is "netbox":
//
//   {
//     "base_url": "https://netbox.example.com",          required, absolute http(s)
//     "token_secret_ref": "${secret:secret:netbox:value}", optional, see below
//     "site_filter": "hq",                                 optional, NetBox site slug
//     "allow_private_network": true                        optional, RFC-1918 targets
//   }
//
// token_secret_ref is empty (anonymous NetBox), the bare name of a stored secret,
// or a full ${secret:<source>:<id|name>:<field>} reference — e.g.
// ${secret:integration:Netbox:token} reuses the token of the NetBox integration.
// A raw token is rejected at write time: `config` is a plain jsonb column that the
// list/get endpoints return to every inventory.read caller, so a token pasted
// there would be stored unencrypted and shown to everyone. Rows written before
// this rule are masked on the way out and still sync (see NetBoxSyncService).
public sealed partial class NetBoxSourceConfig
{
    public const string Type = "netbox";
    public const string Masked = "***";

    public const string BaseUrlKey = "base_url";
    public const string TokenRefKey = "token_secret_ref";
    public const string SiteFilterKey = "site_filter";
    public const string AllowPrivateNetworkKey = "allow_private_network";

    public string BaseUrl { get; init; } = string.Empty;
    public string? TokenSecretRef { get; init; }
    public string? SiteFilter { get; init; }
    public bool AllowPrivateNetwork { get; init; }

    [GeneratedRegex(@"^\$\{secret:(?<source>[a-z_]+):(?<idOrName>[^:}]+):(?<field>[^}]+)\}$")]
    private static partial Regex RefRegex();

    // Same grammar SecretsController accepts for a secret name.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9_-]{0,62}[a-z0-9])?$")]
    private static partial Regex SecretNameRegex();

    public static bool IsNetBox(string? type) =>
        string.Equals(type?.Trim(), Type, StringComparison.OrdinalIgnoreCase);

    public static bool IsReference(string? value) =>
        value is not null && value.Contains("${secret:", StringComparison.Ordinal);

    // Lenient read of whatever is stored; validation happens in Validate.
    public static NetBoxSourceConfig Parse(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return new NetBoxSourceConfig();
        return new NetBoxSourceConfig
        {
            BaseUrl = Str(config, BaseUrlKey)?.Trim() ?? string.Empty,
            TokenSecretRef = NullIfBlank(Str(config, TokenRefKey)),
            SiteFilter = NullIfBlank(Str(config, SiteFilterKey)),
            AllowPrivateNetwork = config.TryGetProperty(AllowPrivateNetworkKey, out var p)
                && p.ValueKind == JsonValueKind.True,
        };
    }

    // Null when base_url is an absolute http(s) URL, otherwise the error to report.
    public static string? ValidateBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "config.base_url is required for a netbox source";
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "config.base_url must be an absolute http(s) URL, e.g. https://netbox.example.com";
        return null;
    }

    // Validates a netbox `config` from a create/update and returns it in stored form:
    // the token reference normalized (bare secret name -> full reference) and every
    // other key kept as sent. `previousTokenRef` is what is stored now: a client that
    // echoes the masked "***" back means "keep it".
    public static async Task<(JsonElement Config, string? Error)> NormalizeAsync(
        JsonElement config, string? previousTokenRef,
        ISecretRepository secrets, IIntegrationRepository integrations, ICredentialRepository credentials,
        CancellationToken ct)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return (config, "config must be an object with base_url (and optionally token_secret_ref, site_filter, allow_private_network)");

        var node = JsonNode.Parse(config.GetRawText())!.AsObject();
        var parsed = Parse(config);

        if (ValidateBaseUrl(parsed.BaseUrl) is { } urlError) return (config, urlError);
        node[BaseUrlKey] = parsed.BaseUrl;

        var raw = parsed.TokenSecretRef?.Trim();
        if (raw == Masked)
        {
            // Echo of a masked legacy value: keep exactly what was stored.
            if (previousTokenRef is null) node.Remove(TokenRefKey);
            else node[TokenRefKey] = previousTokenRef;
        }
        else if (string.IsNullOrEmpty(raw))
        {
            node.Remove(TokenRefKey);
        }
        else
        {
            var (normalized, error) = await NormalizeTokenRefAsync(raw, secrets, integrations, credentials, ct);
            if (error is not null) return (config, error);
            node[TokenRefKey] = normalized;
        }

        return (JsonSerializer.SerializeToElement(node), null);
    }

    private static async Task<(string? Ref, string? Error)> NormalizeTokenRefAsync(
        string value, ISecretRepository secrets, IIntegrationRepository integrations, ICredentialRepository credentials,
        CancellationToken ct)
    {
        var m = RefRegex().Match(value);
        if (m.Success)
        {
            // Catch a typo now rather than as a NetBox 403 at sync time.
            var source = m.Groups["source"].Value;
            var idOrName = m.Groups["idOrName"].Value;
            var isGuid = Guid.TryParse(idOrName, out var id);
            var exists = source switch
            {
                "secret" => await secrets.FindActiveByIdOrNameAsync(id, idOrName, isGuid, ct) is not null,
                "integration" => await integrations.FindActiveByIdOrNameAsync(id, idOrName, isGuid, ct) is not null,
                "credential" => await credentials.FindActiveByIdOrNameAsync(id, idOrName, isGuid, ct) is not null,
                _ => false,
            };
            return exists
                ? (value, null)
                : (null, $"config.token_secret_ref references a {source} that does not exist: {idOrName}");
        }

        if (SecretNameRegex().IsMatch(value)
            && await secrets.FindActiveByIdOrNameAsync(Guid.Empty, value, false, ct) is not null)
            return ($"${{secret:secret:{value}:value}}", null);

        // Never echo the rejected value: it is most likely a pasted raw token.
        return (null,
            "config.token_secret_ref must name a stored secret or be a ${secret:...} reference "
            + "(e.g. ${secret:integration:<name>:token}); raw tokens are not accepted — store the token in the Vault first");
    }

    // The config as the API returns it: a stored literal token becomes "***".
    // References are inert identifiers and pass through.
    public static JsonElement MaskForResponse(string? type, JsonElement config)
    {
        if (!IsNetBox(type) || config.ValueKind != JsonValueKind.Object) return config;
        var token = Str(config, TokenRefKey);
        if (string.IsNullOrEmpty(token) || IsReference(token)) return config;
        var node = JsonNode.Parse(config.GetRawText())!.AsObject();
        node[TokenRefKey] = Masked;
        return JsonSerializer.SerializeToElement(node);
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
