using System.Text.Json;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;

namespace flow_weaver_backend.Services.Ai.Secrets;

// Ciphertext-aware secret resolver. The REST executor passes raw template
// text (URLs, headers, bodies) through SubstituteAsync right before hitting
// the wire, so secrets only live as plaintext inside a single request's
// `HttpRequestMessage`.
//
// Lookups hit the DB on every call — we don't cache because rotation must
// take effect immediately. Load is small: templates rarely have more than
// a couple of ${secret:...} markers, and each is a single indexed lookup.
public sealed class SecretResolver : ISecretResolver
{
    // ${secret:<source>:<id|name>:<field>}
    // source, id/name and field captured as groups 1/2/3. Square bracket
    // syntax not supported — keeps the grammar simple and matches netora.
    private static readonly Regex TokenPattern = new(
        @"\$\{secret:(?<source>[a-z_]+):(?<idOrName>[^:}]+):(?<field>[^}]+)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISecretRepository _secrets;
    private readonly ICredentialRepository _credentials;
    private readonly IAiProviderRepository _providers;
    private readonly IIntegrationRepository _integrations;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IHttpContextAccessor _http;
    private readonly IJwtTokenService _jwt;
    private readonly ITraceLogger _trace;
    private readonly ILogger<SecretResolver> _logger;

    public SecretResolver(
        ISecretRepository secrets,
        ICredentialRepository credentials,
        IAiProviderRepository providers,
        IIntegrationRepository integrations,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IHttpContextAccessor http,
        IJwtTokenService jwt,
        ITraceLogger trace,
        ILogger<SecretResolver> logger)
    {
        _secrets = secrets;
        _credentials = credentials;
        _providers = providers;
        _integrations = integrations;
        _caller = caller;
        _crypto = crypto;
        _http = http;
        _jwt = jwt;
        _trace = trace;
        _logger = logger;
    }

    public Task<string?> ResolveAsync(
        string source, string idOrName, string field, CancellationToken ct)
        => ResolveCoreAsync(source, idOrName, field, ct);

    private async Task<string?> ResolveCoreAsync(
        string source, string idOrName, string field, CancellationToken ct)
    {
        Guid.TryParse(idOrName, out var id);
        var isGuid = id != Guid.Empty;

        try
        {
            var value = source.ToLowerInvariant() switch
            {
                "secret" => await ResolveSecretAsync(id, idOrName, isGuid, field, ct),
                "credential" => await ResolveCredentialAsync(id, idOrName, isGuid, field, ct),
                "ai_provider" => await ResolveAiProviderAsync(id, idOrName, isGuid, field, ct),
                "integration" => await ResolveIntegrationAsync(id, idOrName, isGuid, field, ct),
                "session" => ResolveSession(idOrName, field),
                _ => null,
            };

            // Persisted access record. Until now the only trace of a secret
            // being decrypted was the LogDebug below — which is off at the
            // default log level, and gone as soon as the container is
            // recycled. For a platform whose whole job is holding device
            // credentials, "which credential did that run use, and when" is
            // the central audit question and it had no answer.
            //
            // trace_events rather than audit_logs on purpose: this is a read,
            // it happens once per step per device (so the volume is run-shaped,
            // not admin-shaped), and it wants the retention sweeper. The
            // mutation trail for the same secrets lives in audit_logs.
            //
            // The tuple is safe to persist — `field` is a schema field name
            // ("api_key", "password"), never the value. `value_bytes` is
            // recorded instead of the value so a rotation is still visible as
            // a length change without exposing anything.
            await _trace.EventAsync(
                "secret.access", "security", value is null ? "failed" : "completed",
                metadata: new
                {
                    secret_source = source,
                    id_or_name = idOrName,
                    field,
                    value_bytes = value?.Length,
                },
                error: value is null ? "unresolved" : null,
                ct: ct);

            // Debug-only: we log the key tuple but NEVER the value. `field`
            // is safe (it's a schema field name like "api_key", not the
            // secret itself). Useful when debugging "which secrets does
            // this workflow need?" during audit.
            if (value is null)
            {
                _logger.LogWarning(
                    "secret.resolve.miss source={Source} id_or_name={IdOrName} field={Field}",
                    source, idOrName, field);
            }
            else
            {
                _logger.LogDebug(
                    "secret.resolve.hit source={Source} id_or_name={IdOrName} field={Field} value_bytes={ValueBytes}",
                    source, idOrName, field, value.Length);
            }
            return value;
        }
        catch (Exception ex)
        {
            // Recorded too: a burst of failed resolutions is what a credential
            // being deleted mid-run, or a probe for secrets that don't exist,
            // looks like from the outside.
            await _trace.EventAsync(
                "secret.access", "security", "failed",
                metadata: new { secret_source = source, id_or_name = idOrName, field },
                error: ex.Message,
                ct: ct);

            _logger.LogWarning(ex,
                "secret.resolve.failed source={Source} id_or_name={IdOrName} field={Field}",
                source, idOrName, field);
            return null;
        }
    }

    public Task<string> SubstituteAsync(string template, CancellationToken ct)
        => SubstituteCoreAsync(template, ct);

    private async Task<string> SubstituteCoreAsync(string template, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${secret:", StringComparison.Ordinal))
            return template;

        // Regex.Replace can't await, so we extract matches, resolve in
        // sequence, and rebuild. Sequential is fine here because real-world
        // templates rarely have more than two or three markers.
        var matches = TokenPattern.Matches(template);
        if (matches.Count == 0) return template;

        var sb = new System.Text.StringBuilder(template.Length);
        var cursor = 0;

        foreach (Match m in matches)
        {
            sb.Append(template, cursor, m.Index - cursor);

            var source = m.Groups["source"].Value;
            var idOrName = m.Groups["idOrName"].Value;
            var field = m.Groups["field"].Value;
            var value = await ResolveCoreAsync(source, idOrName, field, ct);

            // Leave the original token in place if unresolved so the user
            // notices in logs instead of silently shipping an empty string.
            sb.Append(value ?? m.Value);
            cursor = m.Index + m.Length;
        }

        sb.Append(template, cursor, template.Length - cursor);
        return sb.ToString();
    }

    // ─── per-source lookups ─────────────────────────────────────────────

    private async Task<string?> ResolveSecretAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        if (!string.Equals(field, "value", StringComparison.OrdinalIgnoreCase))
            return null;

        var row = await _secrets.FindActiveByIdOrNameAsync(id, name, isGuid, ct);
        return row is null ? null : _crypto.Decrypt(row.EncryptedValue);
    }

    private async Task<string?> ResolveCredentialAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        var row = await _credentials.FindActiveByIdOrNameAsync(id, name, isGuid, ct);
        if (row is null) return null;

        return field.ToLowerInvariant() switch
        {
            "username" => row.Username,
            "password" => _crypto.Decrypt(row.EncryptedPassword),
            "private_key" or "privatekey" => _crypto.Decrypt(row.EncryptedPrivateKey),
            _ => null,
        };
    }

    private async Task<string?> ResolveAiProviderAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        if (!string.Equals(field, "api_key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(field, "apikey", StringComparison.OrdinalIgnoreCase))
            return null;

        var row = await _providers.FindActiveByIdOrNameAsync(id, name, isGuid, ct);
        return row is null ? null : _crypto.Decrypt(row.EncryptedApiKey);
    }

    // Resolves the bearer the REST executor uses to call back into our own
    // API under the same permissions as the caller. Accepts only
    // `session:current:jwt` — any other combination returns null and the
    // marker stays literal.
    //
    // Two paths:
    //   - HTTP request present (web chat): reuse the request's own bearer so
    //     the self-call inherits the chatting user's exact token.
    //   - No HTTP request (messaging / scheduled agent runs on the job queue):
    //     mint a short-lived token from the identity already bound on
    //     ICurrentUser. Those roles are the EFFECTIVE roles (already capped
    //     by the channel's MaxRole), so the self-call can never exceed them.
    //     Without this, every agent turn that calls an internal `fw_*` spec
    //     from a background context failed auth (or earlier, on the relative
    //     server URL).
    private string? ResolveSession(string idOrName, string field)
    {
        if (!string.Equals(idOrName, "current", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(field, "jwt", StringComparison.OrdinalIgnoreCase)) return null;

        var header = _http.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header))
        {
            const string prefix = "Bearer ";
            return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? header[prefix.Length..].Trim()
                : null;
        }

        // Background path: no request bearer to borrow. Mint one for the bound
        // identity. If nothing is bound (truly anonymous), leave it unresolved.
        if (!_caller.IsAuthenticated || _caller.UserId == Guid.Empty) return null;
        // Carry the transport capability ceiling (if any) into the minted token
        // so the self-call is narrowed exactly like the native tool path.
        return _jwt.CreateAccessToken(
            _caller.UserId, _caller.Username ?? string.Empty,
            _caller.Roles, _caller.CapabilityCeiling, out _);
    }

    // Integration.AuthConfig is a free-form JSON object; the field is a
    // dotted path evaluated against it (e.g. "token", "basic.password").
    // Keeps the resolver flexible enough for bearer, basic, apiKey, oauth1
    // configurations without new source types.
    private async Task<string?> ResolveIntegrationAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        var row = await _integrations.FindActiveByIdOrNameAsync(id, name, isGuid, ct);
        if (row is null || row.AuthConfig.ValueKind != JsonValueKind.Object) return null;

        var node = row.AuthConfig;
        foreach (var segment in field.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object) return null;
            if (!node.TryGetProperty(segment, out var next)) return null;
            node = next;
        }
        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => node.GetRawText(),
            _ => null,
        };
    }
}
