using System.Net.Http;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Net;
using Microsoft.AspNetCore.Mvc;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

// Probes an integration by GETting BaseURL + HealthCheck.Path and comparing
// the status code against HealthCheck.ExpectedStatus (default 200). The
// check honors Integration.TLSSkipVerify and inherits auth from
// IntegrationAuthBuilder.
//
// Beyond reachability, the lenient (no explicit health config) mode also
// verifies the integration's CREDENTIALS when it has any. Reachability
// alone produced false positives: NetBox's site root answers 200 to
// anonymous requests, so an integration with a revoked API token showed
// "healthy" while every real API call failed 403 Invalid token. See
// ProbeAsync for the verification ladder.
//
// HttpClient is obtained from IHttpClientFactory via two named clients —
// "integration" (validating TLS) and "integration-insecure" (skips TLS
// validation). Using named clients avoids rebuilding the handler per
// request and keeps the connection pool warm.
public class IntegrationHealthChecker : IIntegrationHealthChecker
{
    public const string ClientName = "integration";
    public const string InsecureClientName = "integration-insecure";

    private readonly IRepository<IntegrationModel> _integrations;
    private readonly IIntegrationActionRepository _actions;
    private readonly ICurrentUser _caller;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IIntegrationAuthApplier _auth;
    private readonly ILogger<IntegrationHealthChecker> _logger;

    public IntegrationHealthChecker(
        IRepository<IntegrationModel> integrations,
        IIntegrationActionRepository actions,
        ICurrentUser caller,
        IHttpClientFactory httpClientFactory,
        IIntegrationAuthApplier auth,
        ILogger<IntegrationHealthChecker> logger)
    {
        _integrations = integrations;
        _actions = actions;
        _caller = caller;
        _httpClientFactory = httpClientFactory;
        _auth = auth;
        _logger = logger;
    }

    public async Task<ActionResult<IntegrationHealthCheckResult>> CheckAsync(Guid integrationId, CancellationToken ct)
    {
        var integration = await _integrations.GetByIdAsync(integrationId, ct: ct);

        if (integration is null)
            return new NotFoundObjectResult(new { error = "integration not found" });

        // When the admin didn't configure an explicit health endpoint,
        // we fall back to a *reachability* check — any HTTP response
        // (including 404 from services that don't expose a root path)
        // counts as healthy. Most internal REST services the user wires
        // up (email microservice, Infoblox, ServiceNow) respond 404 on
        // `/` even though they're fully operational, so the old strict
        // "must return 200" default produced false negatives.
        var userConfigured = integration.HealthCheck.ValueKind == JsonValueKind.Object
            && integration.HealthCheck.EnumerateObject().Any();
        var healthConfig = ParseHealthConfig(integration.HealthCheck);
        var result = await ProbeAsync(integration, healthConfig, userConfigured, ct);

        // Persist the probe outcome so the Integrations list view can show
        // it without re-probing every refresh.
        integration.Status = result.Status;
        integration.LastCheckedAt = DateTime.UtcNow;
        integration.UpdatedAt = DateTime.UtcNow;
        await _integrations.SaveChangesAsync(ct);

        return new OkObjectResult(result);
    }

    private async Task<IntegrationHealthCheckResult> ProbeAsync(
        IntegrationModel integration,
        IntegrationHealthCheckConfig health,
        bool strictStatusMatch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(integration.BaseURL))
        {
            return new IntegrationHealthCheckResult
            {
                Status = "unhealthy",
                Error = "base_url is not set on the integration",
            };
        }

        // A FULL URL pasted into health_check.path concatenates into garbage
        // (base + "https://…" probes "<base>/https:/…" and the origin answers
        // a baffling 403/404). Catch the misconfiguration with a precise
        // message before any request is made.
        if (health.Path.Contains("://", StringComparison.Ordinal))
        {
            return new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Unhealthy,
                Expected = strictStatusMatch ? health.ExpectedStatus : 0,
                Error = "health_check.path must be a path RELATIVE to the integration's base URL "
                    + "(e.g. \"/organizations\"), not a full URL — remove the scheme and host.",
            };
        }

        if (!Uri.TryCreate(CombineUrl(integration.BaseURL, health.Path), UriKind.Absolute, out var url))
        {
            return new IntegrationHealthCheckResult
            {
                Status = "unhealthy",
                Error = "base_url + health.path is not a valid absolute URL",
            };
        }

        var clientName = integration.TLSSkipVerify ? InsecureClientName : ClientName;
        var client = _httpClientFactory.CreateClient(clientName);
        var expected = strictStatusMatch ? health.ExpectedStatus : 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var main = await SendProbeAsync(client, url, integration, withAuth: true, ct);
        if (main.TransportError is not null)
        {
            _logger.LogWarning(
                "integration.health.transport_error integration_id={IntegrationId} integration_name={IntegrationName} error={Error} elapsed_ms={Elapsed}",
                integration.IntegrationId, integration.Name, main.TransportError, sw.ElapsedMilliseconds);
            return new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Unhealthy,
                Expected = expected,
                Error = main.TransportError,
            };
        }
        var statusCode = main.StatusCode!.Value;

        // Strict mode: the admin picked a path + expected status — that IS
        // the health contract; honor it exactly and probe nothing else.
        // (Pointing the path at an authenticated endpoint, e.g. NetBox's
        // `/api/`, is also how an admin makes an invalid token fail strictly.)
        if (strictStatusMatch)
        {
            var strictHealthy = statusCode == health.ExpectedStatus;
            return Finish(new IntegrationHealthCheckResult
            {
                Status = strictHealthy ? Engine.IntegrationStatus.Healthy : Engine.IntegrationStatus.Unhealthy,
                StatusCode = statusCode,
                Expected = expected,
                Error = strictHealthy ? null : $"unexpected status code {statusCode}",
            }, integration, sw);
        }

        // Lenient mode (default): the service answered, so it's alive.
        // 5xx still flags — the server is up in the TCP sense but returning
        // errors, which is "unhealthy" by any reasonable definition.
        if (statusCode >= 500)
        {
            return Finish(new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Unhealthy,
                StatusCode = statusCode,
                Expected = expected,
                Error = $"server returned {statusCode}",
            }, integration, sw);
        }

        // 401/403 with credentials attached = the service is reachable but
        // refuses the integration's identity — every real call would fail
        // the same way, so "the service answered" must not count as healthy.
        //
        // WITHOUT credentials it is only "degraded", not unhealthy: plenty of
        // integrations legitimately run credential-less against services whose
        // ROOT is protected but whose actual endpoints are open — the probe
        // can't tell the two apart, so it flags the ambiguity instead of
        // declaring the integration broken.
        if (statusCode is 401 or 403)
        {
            if (main.CredentialsApplied)
            {
                // oauth2_client_credentials: reaching this point means the
                // token GRANT succeeded (a failed grant surfaces as a
                // transport error above), so the authorization server just
                // validated the credentials — this 401/403 is about the
                // probed PATH, not the secret. API roots commonly reject
                // everyone (Action1's /api/3.0/ does), and "rotate the
                // token" would be exactly the wrong advice. Degraded +
                // guidance instead of a false-negative unhealthy.
                var oauth = IntegrationAuthBuilder.TryParse(integration.AuthConfig);
                if (string.Equals(oauth?.Method, Dtos.IntegrationAuthConfig.OAuth2ClientCredentials,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Finish(new IntegrationHealthCheckResult
                    {
                        Status = Engine.IntegrationStatus.Degraded,
                        StatusCode = statusCode,
                        Expected = expected,
                        AuthVerified = true,
                        Error = $"the OAuth token grant succeeded (credentials are valid) but the probed "
                            + $"path returned HTTP {statusCode}. Either the token lacks permissions for that "
                            + "path, or the path is not a real endpoint (API roots often reject everyone). "
                            + "Set health_check to a real API endpoint — e.g. "
                            + "{\"path\":\"/<some-list-endpoint>\",\"expected_status\":200} — to make this "
                            + "check conclusive.",
                    }, integration, sw);
                }

                return Finish(new IntegrationHealthCheckResult
                {
                    Status = Engine.IntegrationStatus.Unhealthy,
                    StatusCode = statusCode,
                    Expected = expected,
                    Error = $"authentication rejected (HTTP {statusCode}) — the service is reachable but refused the integration's credentials; rotate the token/secret",
                }, integration, sw);
            }
            return Finish(new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Degraded,
                StatusCode = statusCode,
                Expected = expected,
                Error = $"the probed path requires authentication (HTTP {statusCode}) and the integration has no credentials configured. If the endpoints you actually use are open, point health_check.path at one of them; otherwise add credentials to the integration.",
            }, integration, sw);
        }

        // Reachable and not an auth error. Without credentials there is
        // nothing more to verify.
        if (!main.CredentialsApplied)
        {
            return Finish(new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Healthy,
                StatusCode = statusCode,
                Expected = expected,
            }, integration, sw);
        }

        // ── Credential verification ─────────────────────────────────────
        // oauth2_client_credentials short-circuit: reaching this point means
        // the token GRANT succeeded (ApplyAsync would have failed the probe
        // otherwise) — the authorization server itself validated the client
        // credentials, which is stronger evidence than any differential
        // probe against the API.
        var parsedAuth = IntegrationAuthBuilder.TryParse(integration.AuthConfig);
        if (string.Equals(parsedAuth?.Method, Dtos.IntegrationAuthConfig.OAuth2ClientCredentials,
                StringComparison.OrdinalIgnoreCase))
        {
            return Finish(new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Healthy,
                StatusCode = statusCode,
                Expected = expected,
                AuthVerified = true,
            }, integration, sw);
        }

        // A 200 from the probed path proves nothing about the token when
        // that path never validates it (NetBox's site root answers 200 to
        // anyone — the "healthy with a revoked token" false positive).
        //
        // Ladder:
        //   1. Derive an API root from the integration's registered action
        //      paths (their common prefix, e.g. `/api/`) and probe it WITH
        //      credentials. Token-validating APIs reject an invalid token
        //      there (401/403) even where the site root is anonymous.
        //   2. Differential probe: re-request the best succeeding URL
        //      WITHOUT credentials. Anonymous rejected + authenticated
        //      accepted = the token was genuinely validated → healthy.
        //   3. Anonymous also accepted = no probed endpoint ever checked
        //      the token → "degraded", telling the admin exactly how to
        //      close the gap (configure health_check.path).
        var verifyUrl = url;
        var apiRoot = CommonApiRoot(await _actions.ListActivePathsByIntegrationAsync(integration.IntegrationId, ct));
        if (apiRoot is not null
            && Uri.TryCreate(CombineUrl(integration.BaseURL, apiRoot), UriKind.Absolute, out var apiRootUrl)
            && apiRootUrl != url)
        {
            var sub = await SendProbeAsync(client, apiRootUrl, integration, withAuth: true, ct);
            if (sub.StatusCode is 401 or 403)
            {
                return Finish(new IntegrationHealthCheckResult
                {
                    Status = Engine.IntegrationStatus.Unhealthy,
                    StatusCode = sub.StatusCode,
                    Expected = expected,
                    Error = $"authentication rejected (HTTP {sub.StatusCode} on {apiRoot}) — the service is reachable but refused the integration's credentials; rotate the token/secret",
                }, integration, sw);
            }
            // Only a non-error answer makes this URL usable for the
            // differential step; on transport error / 5xx fall back to the
            // main URL.
            if (sub.StatusCode is { } s && s < 500) verifyUrl = apiRootUrl;
        }

        var anonymous = await SendProbeAsync(client, verifyUrl, integration, withAuth: false, ct);
        if (anonymous.StatusCode is 401 or 403)
        {
            return Finish(new IntegrationHealthCheckResult
            {
                Status = Engine.IntegrationStatus.Healthy,
                StatusCode = statusCode,
                Expected = expected,
                AuthVerified = true,
            }, integration, sw);
        }

        return Finish(new IntegrationHealthCheckResult
        {
            Status = Engine.IntegrationStatus.Degraded,
            StatusCode = statusCode,
            Expected = expected,
            AuthVerified = false,
            Error = "reachable, but the configured credentials were never validated: every probed "
                + "endpoint also accepts anonymous requests, so an invalid or revoked token would "
                + "go unnoticed by this check. Set health_check.path to an endpoint that requires "
                + "authentication (e.g. NetBox: {\"path\":\"/api/\",\"expected_status\":200}) to "
                + "make token failures show as unhealthy.",
        }, integration, sw);
    }

    private IntegrationHealthCheckResult Finish(
        IntegrationHealthCheckResult result, IntegrationModel integration, System.Diagnostics.Stopwatch sw)
    {
        _logger.LogInformation(
            "integration.health.probe integration_id={IntegrationId} integration_name={IntegrationName} status={Status} status_code={StatusCode} auth_verified={AuthVerified} elapsed_ms={Elapsed}",
            integration.IntegrationId, integration.Name,
            result.Status, result.StatusCode, result.AuthVerified, sw.ElapsedMilliseconds);
        return result;
    }

    // One GET, credentials optional, transport failures folded into the
    // outcome instead of thrown. `CredentialsApplied` reports whether the
    // integration actually contributed any header (auth or custom) — an
    // AuthConfig with method "none" applies nothing, and then there are no
    // credentials to verify.
    private sealed record ProbeOutcome(int? StatusCode, string? TransportError, bool CredentialsApplied);

    private async Task<ProbeOutcome> SendProbeAsync(
        HttpClient client, Uri url, IntegrationModel integration, bool withAuth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Redirect targets get the same private-network policy as the probe
        // itself (SsrfGuardingRedirectHandler validates each hop before
        // following); an on-prem service that 302s stays reachable, a hop to
        // the metadata endpoint does not.
        request.WithPrivateNetworkPolicy(integration.AllowPrivateNetwork);
        var credentialsApplied = false;
        if (withAuth)
        {
            // An oauth2_client_credentials integration performs its token
            // grant here — a failed grant IS an authentication failure, and
            // the most direct proof this probe can produce.
            try
            {
                await _auth.ApplyAsync(request, integration, ct);
            }
            catch (InvalidOperationException ex)
            {
                return new ProbeOutcome(null, "OAuth token grant failed: " + SanitizeMessage(ex.Message), true);
            }
            // Custom headers count too — api keys often ride there.
            credentialsApplied = request.Headers.Any();
        }

        try
        {
            using var response = await client.SendAsync(request, ct);
            return new ProbeOutcome((int)response.StatusCode, null, credentialsApplied);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeOutcome(null, "request timed out", credentialsApplied);
        }
        catch (HttpRequestException ex)
        {
            return new ProbeOutcome(null, SanitizeMessage(ex.Message), credentialsApplied);
        }
    }

    // Longest common directory prefix of the registered action paths,
    // capped at two segments so the probe stays cheap (an API root like
    // `/api/` or `/rest/v2/`, never a concrete collection endpoint).
    // Templated segments (`{id}`) end the prefix — they aren't probeable.
    // Returns null when there are no usable paths.
    internal static string? CommonApiRoot(IReadOnlyList<string> paths)
    {
        var segmented = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(s => s.Length > 0)
            .ToList();
        if (segmented.Count == 0) return null;

        var prefix = new List<string>();
        for (var i = 0; i < 2 && i < segmented[0].Length; i++)
        {
            var candidate = segmented[0][i];
            if (candidate.Contains('{')) break;
            if (segmented.Any(s => s.Length <= i
                || !s[i].Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                break;
            prefix.Add(candidate);
        }
        return prefix.Count == 0 ? null : "/" + string.Join('/', prefix) + "/";
    }

    // Joins base + path without doubling or losing a slash.
    private static string CombineUrl(string baseUrl, string path)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!trimmedPath.StartsWith('/')) trimmedPath = "/" + trimmedPath;
        return trimmedBase + trimmedPath;
    }

    private static IntegrationHealthCheckConfig ParseHealthConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return new IntegrationHealthCheckConfig();

        var parsed = JsonSerializer.Deserialize<IntegrationHealthCheckConfig>(config.GetRawText())
                     ?? new IntegrationHealthCheckConfig();
        // Trim — a copy-pasted path routinely carries stray whitespace, which
        // would otherwise end up percent-encoded inside the probe URL.
        parsed.Path = parsed.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parsed.Path)) parsed.Path = "/";
        if (parsed.ExpectedStatus <= 0) parsed.ExpectedStatus = 200;
        return parsed;
    }

    // Strips anything that looks like a secret before surfacing upstream
    // errors to the caller. Defensive — HttpClient shouldn't leak headers
    // in exception messages, but we never want to be the one that does.
    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return "request failed";
        if (message.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return "request failed (redacted)";
        }
        return message.Length > 200 ? message[..200] : message;
    }
}
