using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Reports;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Ai.RestExecutor;

// Parses the single AiApiSpec row that contains the requested operation
// on demand — we don't keep the full OpenAPI AST in memory because the
// discover/detail tools only need metadata, and `execute` fires
// infrequently compared to them. Each call reparses once (<50ms) so the
// steady-state index (YamlSpecIndex) can stay lean.
public sealed class RestOperationExecutor : IRestOperationExecutor
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options",
    };

    private readonly IAiApiSpecRepository _specs;
    private readonly IIntegrationRepository _integrations;
    private readonly IApiSpecIndex _index;
    private readonly ISecretResolver _secrets;
    private readonly IReportReferenceResolver _reportRefs;
    private readonly ICurrentUser _caller;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IIntegrationAuthApplier _integrationAuth;
    private readonly ILogger<RestOperationExecutor> _logger;

    // Loopback address of this backend, used to resolve a spec's relative
    // `servers: [- url: /api]` (the fw_* specs) for every caller — web chat,
    // messaging and scheduled agent runs alike. Override with
    // Backend:SelfBaseUrl (env Backend__SelfBaseUrl) when Kestrel is not on
    // the default port; the default matches the container's internal port.
    private readonly string _selfBaseUrl;

    public RestOperationExecutor(
        IAiApiSpecRepository specs,
        IIntegrationRepository integrations,
        IApiSpecIndex index,
        ISecretResolver secrets,
        IReportReferenceResolver reportRefs,
        ICurrentUser caller,
        IUrlGuard urlGuard,
        IHttpClientFactory httpFactory,
        IIntegrationAuthApplier integrationAuth,
        IConfiguration config,
        ILogger<RestOperationExecutor> logger)
    {
        _specs = specs;
        _integrations = integrations;
        _index = index;
        _secrets = secrets;
        _reportRefs = reportRefs;
        _caller = caller;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _integrationAuth = integrationAuth;
        var configured = config["Backend:SelfBaseUrl"];
        _selfBaseUrl = string.IsNullOrWhiteSpace(configured)
            ? "http://localhost:8080"
            : configured.Trim();
        _logger = logger;
    }

    public async Task<RestExecutionResult> ExecuteAsync(
        string operationId,
        JsonElement pathParams,
        JsonElement queryParams,
        JsonElement body,
        CancellationToken ct)
    {
        // Tie the call's wall-clock to RequestTimeout so a hanging upstream
        // can't stretch the agent's chat deadline past its budget.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        var opMeta = _index.GetByOperationId(operationId);
        if (opMeta is null)
            return Fail($"operation '{operationId}' not found");

        var specRow = await _specs.FindActiveByApiAsync(opMeta.Api, cts.Token);
        if (specRow is null)
            return Fail($"spec '{opMeta.Api}' is not available");

        SpecView spec;
        try
        {
            spec = ParseSpec(specRow.Content, opMeta.Method, opMeta.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spec parse failed for {Api}/{OpId}", opMeta.Api, operationId);
            return Fail($"spec parse failed: {ex.Message}");
        }

        // Scoped integration (optional). When present, its BaseURL + AuthConfig
        // take priority over whatever the spec declares — the user-configured
        // integration is the authoritative target. Falls back to the spec's
        // own `servers:` + `securitySchemes` when the spec is global
        // (integration_id null).
        IntegrationModel? integration = null;
        if (specRow.IntegrationId is Guid integrationId)
        {
            integration = await _integrations.GetByIdAsync(integrationId, tracking: false, ct: cts.Token);
        }

        var serverUrl = integration?.BaseURL;
        if (string.IsNullOrWhiteSpace(serverUrl))
            serverUrl = spec.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl))
            return Fail(
                integration is null
                    ? $"spec '{opMeta.Api}' declares no server URL"
                    : $"integration '{integration.Name}' has no base_url set");

        // Substitute ${secret:...} in the server URL + auth payload before
        // any network touches them. Failures here leave the marker in
        // place so logs surface the unresolved reference.
        var baseUrl = await _secrets.SubstituteAsync(serverUrl, cts.Token);

        // Specs that target the backend itself declare `servers: [- url: /api]`.
        // A path with no scheme means "this process", so it is resolved against
        // the backend's own loopback address (Backend:SelfBaseUrl) — never
        // against the incoming request's Host. That header is whatever the
        // browser typed, rewritten by the forwarded-headers middleware: behind
        // the frontend proxy in Docker it read `localhost:3000`, and the backend
        // dialled port 3000 inside its own container and got "connection
        // refused" on every fw_* call. Even with a public hostname it would
        // mean calling ourselves through the ingress (VPN-only boxes, hairpin
        // NAT, the SSRF guard) for a request that never needs to leave the
        // process. External APIs keep their absolute URL.
        var isLocalBackend = !Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute);
        if (isLocalBackend)
            baseUrl = _selfBaseUrl.TrimEnd('/') + baseUrl;

        var (url, query) = BuildPathAndQuery(opMeta.Path, pathParams, queryParams);

        // Fail fast when the agent forgot to supply `path_params` for a
        // templated operation. Without this guard the literal `{id}` gets
        // URL-encoded downstream and the backend returns 404 with zero
        // actionable context. Surfacing the unfilled placeholders here
        // tells the agent exactly what to add to its next call.
        var unresolved = FindUnresolvedPathPlaceholders(url);
        if (unresolved.Count > 0)
            return Fail(
                $"operation '{operationId}' requires path_params for " +
                $"{string.Join(", ", unresolved.Select(p => "'" + p + "'"))}. " +
                "Pass them via the `path_params` argument, e.g. " +
                "`{\"path_params\": {\"id\": \"...\"}}`.");

        var fullUrl = CombineUrl(baseUrl, url, query);

        // SSRF guard policy:
        //   - Same-origin (the backend talking to itself): trivially safe.
        //   - Integration-scoped: the BaseURL was explicitly configured by
        //     an Operator via /integrations. Internal/private ranges are the
        //     norm for on-prem tools (NetBox, AWX, ServiceNow in a VPN) so
        //     we trust the admin's configuration and skip the guard.
        //   - Everything else (global spec with a literal `servers:` URL):
        //     keep the guard on so the agent can't be coerced into hitting
        //     metadata endpoints or lateral internal services.
        if (!isLocalBackend && integration is null)
        {
            try { _urlGuard.EnsureSafe(fullUrl); }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
        }

        using var msg = new HttpRequestMessage(new HttpMethod(opMeta.Method), fullUrl);

        // Redirect targets are validated per hop by SsrfGuardingRedirectHandler
        // (the initial URL was handled just above). The two cases exempted from
        // the initial guard get allowPrivate rather than a full exemption: a
        // same-origin backend call and an operator-configured integration may
        // legitimately land on 10/8, but neither has any business being
        // redirected to 169.254.169.254 — and allowPrivate never unblocks that.
        msg.WithPrivateNetworkPolicy(isLocalBackend || integration is not null);

        // JSON body — only sent when the spec declares a request body and
        // the caller provided something.
        if (spec.HasJsonBody && body.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var bodyText = await _secrets.SubstituteAsync(body.GetRawText(), cts.Token);

            // Expand `${report:<id>}` attachment references into base64
            // server-side so the agent references a persisted report by its
            // short id instead of copying the blob (which it truncates, and
            // which makes bundling several formats into one email
            // impractical). A bad reference is a hard failure — an
            // actionable error beats a corrupt attachment on the wire.
            try
            {
                bodyText = await _reportRefs.SubstituteAsync(bodyText, cts.Token);
            }
            catch (ReportReferenceException ex)
            {
                _logger.LogWarning(
                    "rest.executor.report_reference_failed operation_id={OperationId} reason={Reason}",
                    operationId, ex.Message);
                return Fail(ex.Message);
            }

            msg.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
        }

        // Auth resolution:
        //   - If the spec is scoped to an integration, apply the integration's
        //     auth_config (token / basic / api_key / oauth2_client_credentials)
        //     + custom Headers. This is the explicit user-configured path and
        //     always wins. It must go through the async applier: the sync
        //     builder skips oauth2_client_credentials, so the call went out
        //     without an Authorization header.
        //   - Otherwise, fall back to the spec's first-matching security
        //     scheme via x-credential-ref. Global security first, op-level
        //     overrides.
        if (integration is not null)
        {
            try
            {
                await _integrationAuth.ApplyAsync(msg, integration, cts.Token);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "rest.executor.auth_failed operation_id={OperationId} integration_id={IntegrationId}",
                    operationId, integration.IntegrationId);
                return Fail($"integration '{integration.Name}' auth failed: {ex.Message}");
            }
        }
        else
        {
            await ApplyAuthAsync(msg, spec, cts.Token);
        }

        var client = _httpFactory.CreateClient("rest_call");

        // Host-only log (path can carry path params). We never log the
        // response body — it may contain sensitive data — but status +
        // duration is enough to debug "is NetBox slow / returning 500?".
        string urlHost = string.Empty;
        try { urlHost = new Uri(fullUrl).Host; } catch { /* best effort */ }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "rest.executor.begin operation_id={OperationId} method={Method} host={Host} integration_id={IntegrationId}",
            operationId, opMeta.Method, urlHost, integration?.IntegrationId);

        try
        {
            using var resp = await client.SendAsync(msg, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            var parsed = TryParseJson(raw);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);

            _logger.LogInformation(
                "rest.executor.end operation_id={OperationId} status_code={StatusCode} elapsed_ms={Elapsed} response_bytes={ResponseBytes}",
                operationId, (int)resp.StatusCode, sw.ElapsedMilliseconds, raw.Length);

            return new RestExecutionResult
            {
                StatusCode = (int)resp.StatusCode,
                Body = parsed,
                Headers = headers,
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "rest.executor.timeout operation_id={OperationId} host={Host} elapsed_ms={Elapsed} limit_s={LimitSeconds}",
                operationId, urlHost, sw.ElapsedMilliseconds, RequestTimeout.TotalSeconds);
            return Fail($"request timed out after {RequestTimeout.TotalSeconds:n0}s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "rest.executor.http_error operation_id={OperationId} host={Host} elapsed_ms={Elapsed}",
                operationId, urlHost, sw.ElapsedMilliseconds);
            return Fail($"http error: {ex.Message}");
        }
    }

    // ─── spec parsing ───────────────────────────────────────────────────

    private static SpecView ParseSpec(string yaml, string method, string path)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new SpecView();

        var view = new SpecView
        {
            ServerUrl = ExtractServerUrl(root),
        };

        ExtractSecuritySchemes(root, view);
        view.GlobalSecurity = ExtractSecurityRefs(root, "security");

        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths)
            return view;

        if (!paths.Children.TryGetValue(new YamlScalarNode(path), out var pathNode)
            || pathNode is not YamlMappingNode methods)
            return view;

        if (!methods.Children.TryGetValue(new YamlScalarNode(method.ToLowerInvariant()), out var opNode)
            || opNode is not YamlMappingNode op)
            return view;

        view.OperationSecurity = ExtractSecurityRefs(op, "security");
        view.HasJsonBody = DetectJsonBody(op);
        return view;
    }

    private static string? ExtractServerUrl(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("servers"), out var serversNode)
            || serversNode is not YamlSequenceNode servers
            || servers.Children.Count == 0)
            return null;
        if (servers.Children[0] is not YamlMappingNode first) return null;
        return first.Children.TryGetValue(new YamlScalarNode("url"), out var urlNode)
               && urlNode is YamlScalarNode urlScalar
            ? urlScalar.Value
            : null;
    }

    private static void ExtractSecuritySchemes(YamlMappingNode root, SpecView view)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("components"), out var compsNode)
            || compsNode is not YamlMappingNode comps) return;
        if (!comps.Children.TryGetValue(new YamlScalarNode("securitySchemes"), out var schemesNode)
            || schemesNode is not YamlMappingNode schemes) return;

        foreach (var entry in schemes)
        {
            if (entry.Key is not YamlScalarNode nameNode) continue;
            if (entry.Value is not YamlMappingNode schemeNode) continue;

            var scheme = new SecurityScheme
            {
                Name = nameNode.Value ?? string.Empty,
                Type = GetScalar(schemeNode, "type") ?? string.Empty,
                In = GetScalar(schemeNode, "in"),
                ParamName = GetScalar(schemeNode, "name"),
                HttpScheme = GetScalar(schemeNode, "scheme"),
                BearerFormat = GetScalar(schemeNode, "bearerFormat"),
                // Non-standard extensions: we let the spec carry a literal
                // `x-credential-ref` with a ${secret:...} marker so admins
                // configure secrets once and the executor pulls them at
                // call time. Absence = anonymous.
                CredentialRef = GetScalar(schemeNode, "x-credential-ref"),
            };
            view.SecuritySchemes[scheme.Name] = scheme;
        }
    }

    private static List<string> ExtractSecurityRefs(YamlMappingNode scope, string key)
    {
        var refs = new List<string>();
        if (!scope.Children.TryGetValue(new YamlScalarNode(key), out var secNode)
            || secNode is not YamlSequenceNode sec) return refs;

        foreach (var requirement in sec)
        {
            if (requirement is not YamlMappingNode reqMap) continue;
            foreach (var entry in reqMap)
            {
                if (entry.Key is YamlScalarNode name && name.Value is not null)
                    refs.Add(name.Value);
            }
        }
        return refs;
    }

    private static bool DetectJsonBody(YamlMappingNode op)
    {
        if (!op.Children.TryGetValue(new YamlScalarNode("requestBody"), out var rb)
            || rb is not YamlMappingNode rbMap) return false;
        if (!rbMap.Children.TryGetValue(new YamlScalarNode("content"), out var content)
            || content is not YamlMappingNode contentMap) return false;
        return contentMap.Children.Keys
            .OfType<YamlScalarNode>()
            .Any(k => (k.Value ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s
            ? s.Value
            : null;

    // ─── URL + auth helpers ─────────────────────────────────────────────

    private static (string path, List<KeyValuePair<string, string>> query) BuildPathAndQuery(
        string template, JsonElement pathParams, JsonElement queryParams)
    {
        var path = template;
        if (pathParams.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in pathParams.EnumerateObject())
            {
                var encoded = Uri.EscapeDataString(prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText());
                path = path.Replace("{" + prop.Name + "}", encoded, StringComparison.Ordinal);
            }
        }

        var query = new List<KeyValuePair<string, string>>();
        if (queryParams.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in queryParams.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null
                    || prop.Value.ValueKind == JsonValueKind.Undefined) continue;
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
                query.Add(new KeyValuePair<string, string>(prop.Name, value));
            }
        }
        return (path, query);
    }

    private static List<string> FindUnresolvedPathPlaceholders(string path)
    {
        var names = new List<string>();
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '{') continue;
            var end = path.IndexOf('}', i + 1);
            if (end < 0) break;
            var inner = path.Substring(i + 1, end - i - 1);
            if (inner.Length > 0 && inner.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                names.Add(inner);
            i = end;
        }
        return names;
    }

    private static string CombineUrl(string baseUrl, string path, List<KeyValuePair<string, string>> query)
    {
        var joined = baseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
        if (query.Count == 0) return joined;
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return joined.Contains('?', StringComparison.Ordinal)
            ? joined + "&" + qs
            : joined + "?" + qs;
    }

    private async Task ApplyAuthAsync(HttpRequestMessage msg, SpecView spec, CancellationToken ct)
    {
        // Op-level security overrides global; first scheme wins. Empty list
        // means "anonymous" — don't touch headers.
        var refs = spec.OperationSecurity.Count > 0 ? spec.OperationSecurity : spec.GlobalSecurity;
        foreach (var name in refs)
        {
            if (!spec.SecuritySchemes.TryGetValue(name, out var scheme)) continue;
            if (string.IsNullOrEmpty(scheme.CredentialRef)) continue;

            var value = await _secrets.SubstituteAsync(scheme.CredentialRef, ct);
            if (string.IsNullOrEmpty(value)) continue;

            switch (scheme.Type.ToLowerInvariant())
            {
                case "http" when string.Equals(scheme.HttpScheme, "bearer", StringComparison.OrdinalIgnoreCase):
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
                    return;
                case "http" when string.Equals(scheme.HttpScheme, "basic", StringComparison.OrdinalIgnoreCase):
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                    return;
                case "apikey" when string.Equals(scheme.In, "header", StringComparison.OrdinalIgnoreCase):
                    msg.Headers.TryAddWithoutValidation(scheme.ParamName ?? "X-API-Key", value);
                    return;
                case "apikey" when string.Equals(scheme.In, "query", StringComparison.OrdinalIgnoreCase):
                    var sep = msg.RequestUri!.Query.Length == 0 ? "?" : "&";
                    msg.RequestUri = new Uri(
                        msg.RequestUri + sep
                        + Uri.EscapeDataString(scheme.ParamName ?? "api_key")
                        + "=" + Uri.EscapeDataString(value));
                    return;
            }
        }
    }

    private static JsonElement TryParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return JsonDocument.Parse("null").RootElement;
        try { return JsonDocument.Parse(raw).RootElement; }
        catch
        {
            return JsonSerializer.SerializeToElement(new { raw });
        }
    }

    private static RestExecutionResult Fail(string error) => new()
    {
        StatusCode = 0,
        Body = JsonDocument.Parse("null").RootElement,
        Error = error,
    };

    // ─── local types ────────────────────────────────────────────────────

    private sealed class SpecView
    {
        public string? ServerUrl { get; set; }
        public bool HasJsonBody { get; set; }
        public Dictionary<string, SecurityScheme> SecuritySchemes { get; } =
            new(StringComparer.Ordinal);
        public List<string> GlobalSecurity { get; set; } = new();
        public List<string> OperationSecurity { get; set; } = new();
    }

    private sealed class SecurityScheme
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string? In { get; init; }
        public string? ParamName { get; init; }
        public string? HttpScheme { get; init; }
        public string? BearerFormat { get; init; }
        public string? CredentialRef { get; init; }
    }
}
