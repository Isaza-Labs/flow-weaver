using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Executes an integration action: reads the integration row for auth + base
// URL, reads the action row for method + path + body template, applies
// IntegrationAuthBuilder, fires the HTTP request, returns the response.
//
// Input expects `{ "integration_id": "...", "action_id": "...",
// "body": {...}, "params": { "device_id": "..." }, "query": { ... } }`.
public sealed class IntegrationActionHandler : ISnippetHandler
{
    public string Type => "integration_action";
    // Integration actions can be GET (read) or POST/PUT/DELETE (mutate).
    // The handler can't tell at registration time, so we default to
    // RequiresCompensation. Authors override per-snippet when the
    // action is verifiably read-only.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly IIntegrationRepository _integrations;
    private readonly IIntegrationActionRepository _actions;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IIntegrationAuthApplier _auth;
    private readonly IUrlGuard _urlGuard;
    private readonly ILogger<IntegrationActionHandler> _logger;

    public IntegrationActionHandler(
        IIntegrationRepository integrations,
        IIntegrationActionRepository actions,
        IHttpClientFactory httpFactory,
        IIntegrationAuthApplier auth,
        IUrlGuard urlGuard,
        ILogger<IntegrationActionHandler> logger)
    {
        _integrations = integrations;
        _actions = actions;
        _httpFactory = httpFactory;
        _auth = auth;
        _urlGuard = urlGuard;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        // `path_params` / `query_params` are the portable spellings of
        // `params` / `query` (snippets/SPEC.md `integration_action`).
        var input = PayloadAliases.Normalize(request.SnippetType, request.InputPayload);

        if (!TryReadGuid(input, "integration_id", out var integrationId, out var integrationIdErr))
            return Fail(integrationIdErr);
        if (!TryReadGuid(input, "action_id", out var actionId, out var actionIdErr))
            return Fail(actionIdErr);

        var integration = await _integrations.FindActiveByIdAsync(integrationId, ct);
        if (integration is null) return Fail($"integration {integrationId} not found");

        // S15.1 — block dispatch when the integration is still waiting
        // for credentials (typical state right after the import wizard
        // auto-creates it). Surfacing a clear error here beats a 401 from
        // the upstream system or a confusing HTTPS handshake failure.
        if (string.Equals(integration.Status,
                flow_weaver_backend.Services.Engine.IntegrationStatus.NeedsConfig,
                StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                $"integration '{integration.Name}' needs configuration. " +
                $"Add credentials in /integrations/{integration.IntegrationId} before running.");
        }

        var action = await _actions.FindActiveByIdAsync(actionId, ct);
        if (action is null) return Fail($"integration action {actionId} not found");

        var path = InterpolatePath(action.Path, input);
        var url = integration.BaseURL.TrimEnd('/') + "/" + path.TrimStart('/');

        // Host-only log — full URL may contain interpolated params or query.
        string urlHost = string.Empty;
        try { urlHost = new Uri(url).Host; } catch { /* best effort */ }

        _logger.LogDebug(
            "worker.integration_action.start step_run_id={StepRunId} device_id={DeviceId} integration_id={IntegrationId} action_id={ActionId} action_name={ActionName} method={Method} url_host={UrlHost}",
            request.StepRunId, request.DeviceId, integrationId, actionId, action.Name, action.Method, urlHost);

        // Append query params.
        if (input.TryGetProperty("query", out var qp) && qp.ValueKind == JsonValueKind.Object)
        {
            var qs = string.Join("&", qp.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value.GetString()!)}"));
            if (!string.IsNullOrEmpty(qs))
                url += (url.Contains('?') ? "&" : "?") + qs;
        }

        // SSRF guard. Block private/loopback/metadata-IP targets even when
        // an admin registered the Integration with a malicious BaseURL.
        // The integration's AllowPrivateNetwork flag relaxes the RFC-1918
        // check ONLY (loopback + metadata-IP stay blocked) so a self-hosted
        // deployment with an internal NetBox / similar service can still
        // call it. Loopback / 169.254.169.254 are still blocked.
        try
        {
            _urlGuard.EnsureSafe(url, allowPrivate: integration.AllowPrivateNetwork);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        var method = new HttpMethod(string.IsNullOrWhiteSpace(action.Method) ? "GET" : action.Method);
        using var msg = new HttpRequestMessage(method, url);

        // Async: an oauth2_client_credentials integration obtains its Bearer
        // token here (cached across calls). A failed grant is a clean step
        // failure, not an anonymous upstream 401.
        try
        {
            await _auth.ApplyAsync(msg, integration, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Fail($"integration auth failed: {ex.Message}");
        }

        if (input.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Undefined)
        {
            msg.Content = new StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json");
        }

        // Carry the same private-network policy onto redirect targets, which
        // SsrfGuardingRedirectHandler validates before following. Without this
        // a hop would be judged with allowPrivate=false and an on-prem
        // integration that redirects internally would break.
        msg.WithPrivateNetworkPolicy(integration.AllowPrivateNetwork);

        var clientName = integration.TLSSkipVerify
            ? IntegrationHealthChecker.InsecureClientName
            : IntegrationHealthChecker.ClientName;
        var client = _httpFactory.CreateClient(clientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "worker.integration_action.failed step_run_id={StepRunId} integration_id={IntegrationId} action_id={ActionId} action_name={ActionName} url_host={UrlHost}",
                request.StepRunId, integrationId, actionId, action.Name, urlHost);
            return Fail($"integration request failed: {ex.Message}");
        }

        // The using ensures the underlying socket goes back to the pool
        // even when the body parse path below throws or the response is
        // bigger than the dispatcher cares to read.
        using var _responseScope = response;

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var headers = new Dictionary<string, string>();
        foreach (var h in response.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

        // Parse the response as JSON when the server advertises it.
        // Exposing `data` alongside the raw `body` means downstream
        // templates can reach fields directly: `{{ steps.X.output.data.results[0].name }}`
        // instead of the ugly (and broken) `output.body.results` — which
        // used to trip up every agent-generated workflow because
        // `body` is a string. Back-compat is preserved: `body` still
        // carries the raw text for templates that rely on it.
        //
        // Content-Type match is lenient (`application/json`,
        // `application/hal+json`, `application/vnd.netbox+json`, etc.)
        // because NetBox/Infoblox/ServiceNow each have their own flavor.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var looksLikeJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        JsonElement? parsedData = null;
        if (looksLikeJson && !string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var parsed = JsonDocument.Parse(responseBody);
                parsedData = parsed.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                // Non-fatal: the server lied about Content-Type, or sent
                // partial JSON. Log a warning so the operator can fix the
                // upstream, and fall through with `data = null`.
                _logger.LogWarning(
                    "worker.integration_action.body_parse_failed step_run_id={StepRunId} integration_id={IntegrationId} action_id={ActionId} content_type={ContentType} error={Error}",
                    request.StepRunId, integrationId, actionId, contentType, ex.Message);
            }
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            status_code = (int)response.StatusCode,
            body = responseBody,
            data = parsedData,
            content_type = contentType,
            headers,
        });

        var ok = response.IsSuccessStatusCode;
        _logger.LogInformation(
            "worker.integration_action.ok step_run_id={StepRunId} integration_id={IntegrationId} action_id={ActionId} action_name={ActionName} status_code={StatusCode} response_bytes={ResponseBytes}",
            request.StepRunId, integrationId, actionId, action.Name, (int)response.StatusCode, responseBody?.Length ?? 0);

        return new SnippetResult
        {
            Success = ok,
            Output = output,
            // The catalogued verb is the evidence, and only a call that succeeded can
            // have changed anything.
            //
            // Nashira also consults an explicit `ReadOnly` flag on the catalogued action
            // and trusts it over the verb; this product's IntegrationAction carries no
            // such column, so the verb is all the evidence there is. Recorded rather than
            // papered over — the two handlers agree on every action whose flag matches its
            // verb, and a read-only POST is the case where they would not. Closing that
            // gap is a data-model change, not this one.
            Change = ok && HttpVerbs.Mutating(action.Method)
                ? StepChange.Changed : StepChange.Unchanged,
            Logs = $"{method} {url} → {(int)response.StatusCode}",
            Error = ok ? string.Empty : $"HTTP {(int)response.StatusCode}",
        };
    }

    private static string InterpolatePath(string template, JsonElement input)
    {
        if (!input.TryGetProperty("params", out var prms) || prms.ValueKind != JsonValueKind.Object)
            return template;

        var result = template;
        foreach (var prop in prms.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                result = result.Replace($"{{{prop.Name}}}", prop.Value.GetString());
        }
        return result;
    }

    // Splits the two failure modes so the caller can surface which one
    // happened. Conflating them produced the long-running bug where the
    // agent kept reshaping the payload because every failure read as
    // "field is required" even when the field was present with a bogus
    // value (e.g. a human-readable action name instead of a GUID).
    private static bool TryReadGuid(JsonElement el, string key, out Guid value, out string error)
    {
        value = Guid.Empty;
        error = string.Empty;
        if (!el.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null
            || v.ValueKind == JsonValueKind.Undefined)
        {
            error = $"input.{key} is required";
            return false;
        }
        if (v.ValueKind != JsonValueKind.String || !Guid.TryParse(v.GetString(), out value))
        {
            var raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            error = $"input.{key} must be a GUID (got '{raw}'). Use list_palette_actions to resolve the real id.";
            return false;
        }
        return true;
    }

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
