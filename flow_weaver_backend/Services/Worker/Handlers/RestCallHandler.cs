using System.Text.Json;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Net;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Makes an HTTP request. Input: `{ "url": "...", "method": "GET",
// "headers": { "X-Custom": "val" }, "body": "..." }`.
// Output: `{ "status_code": 200, "body": "...", "headers": { ... } }`.
public sealed class RestCallHandler : ISnippetHandler
{
    public string Type => "rest_call";
    // GET is idempotent; PUT/PATCH/DELETE are not. The author overrides
    // per-snippet when they know the verb at design time.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IUrlGuard _urlGuard;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<RestCallHandler> _logger;

    public RestCallHandler(
        IHttpClientFactory httpFactory,
        IUrlGuard urlGuard,
        ISecretResolver secrets,
        ILogger<RestCallHandler> logger)
    {
        _httpFactory = httpFactory;
        _urlGuard = urlGuard;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.InputPayload;

        // `rest_call` has two forms under one type (snippets/SPEC.md
        // `rest_call`): the raw one this handler implements, and the
        // catalogued one (`source` + `operation_id`) that resolves an
        // operation out of a stored API spec. This product has no such
        // catalogue for workflow steps — its equivalent is an
        // `integration_action` node — so a catalogued step fails naming the
        // keys it carries. "input.url is required" is true and useless; the
        // author needs to know the FORM is unsupported, not that a key is
        // absent.
        var catalogued = PayloadAliases.CataloguedRestCall(input);
        if (catalogued.Count > 0)
            return Fail(PayloadAliases.CataloguedRestCallError(catalogued));

        // "`${secret:...}` references inside `headers` and `url` are resolved at
        // run time" (snippets/SPEC.md `rest_call`). Before the parse and before
        // the SSRF guard: a token in the path is part of the address, and the
        // guard must see the address that will actually be dialled — not a
        // marker that happens to parse.
        //
        // `declaredUrl` keeps the form the author wrote. It is what goes in the
        // step log, because the resolved one may carry the token the reference
        // exists to keep out of writing.
        var declaredUrl = input.TryGetProperty("url", out var u) ? u.GetString() : null;
        var url = await ResolveSecretsAsync(declaredUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
            return Fail("input.url is required");

        // Most common agent-authored mistake: the LLM takes an operationId
        // from a spec (`fw_email:/mail/send-with-attachment`) and shoves it
        // into a rest_call URL. The Uri parser rejects it with a generic
        // message; intercept and explain the right path so the user/agent
        // can fix the node instead of guessing.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            if (url.Contains(':') && !url.Contains("://"))
            {
                return Fail(
                    $"`{url}` looks like an operationId (e.g. `fw_email:/path`). "
                    + "rest_call needs a full HTTP URL. Either use the `integration_action` "
                    + "snippet type pointing at the registered Integration + action, or "
                    + "put the absolute URL here (e.g. `http://host:port/path`).");
            }
            return Fail($"`{url}` is not a valid http/https URL");
        }

        // Sprint 5.1: SSRF guard — reject private/loopback/metadata URLs.
        try
        {
            _urlGuard.EnsureSafe(url);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        var method = input.TryGetProperty("method", out var m)
            ? m.GetString()?.ToUpperInvariant() ?? "GET"
            : "GET";

        var httpMethod = new HttpMethod(method);
        using var msg = new HttpRequestMessage(httpMethod, url);

        // Host-only — query string / path may carry secrets.
        var urlHost = parsed.Host;

        _logger.LogDebug(
            "worker.rest.start step_run_id={StepRunId} device_id={DeviceId} method={Method} url_host={UrlHost}",
            request.StepRunId, request.DeviceId, method, urlHost);

        if (input.TryGetProperty("headers", out var hdrs) && hdrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in hdrs.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                // An Authorization header is the single most common home for a
                // `${secret:...}` reference; sent literally it becomes a 401
                // whose cause is invisible in the response.
                msg.Headers.TryAddWithoutValidation(
                    prop.Name, await ResolveSecretsAsync(prop.Value.GetString(), ct));
            }
        }

        if (input.TryGetProperty("body", out var body))
        {
            var bodyStr = body.ValueKind == JsonValueKind.String
                ? body.GetString() ?? string.Empty
                : body.GetRawText();
            msg.Content = new StringContent(bodyStr, System.Text.Encoding.UTF8, "application/json");
        }

        var client = _httpFactory.CreateClient("rest_call");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "worker.rest.failed step_run_id={StepRunId} method={Method} url_host={UrlHost} reason={Reason}",
                request.StepRunId, method, urlHost, "request_exception");
            return Fail($"request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                "worker.rest.failed step_run_id={StepRunId} method={Method} url_host={UrlHost} reason={Reason}",
                request.StepRunId, method, urlHost, "timeout");
            return Fail("request timed out");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var responseHeaders = new Dictionary<string, string>();
        foreach (var h in response.Headers)
            responseHeaders[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers)
            responseHeaders[h.Key] = string.Join(", ", h.Value);

        var output = JsonSerializer.SerializeToElement(new
        {
            status_code = (int)response.StatusCode,
            body = responseBody,
            headers = responseHeaders,
        });

        var success = response.IsSuccessStatusCode;
        _logger.LogInformation(
            "worker.rest.ok step_run_id={StepRunId} method={Method} url_host={UrlHost} status_code={StatusCode} response_bytes={ResponseBytes}",
            request.StepRunId, method, urlHost, (int)response.StatusCode, responseBody?.Length ?? 0);

        return new SnippetResult
        {
            Success = success,
            Output = output,
            // The verb is the evidence: a GET that came back 200 changed nothing, a POST
            // that came back 200 did. Measured rather than deferred to the author, and it
            // only counts if the call actually succeeded.
            Change = success && HttpVerbs.Mutating(method) ? StepChange.Changed : StepChange.Unchanged,
            Logs = $"{method} {declaredUrl} → {(int)response.StatusCode}",
            Error = success ? string.Empty : $"HTTP {(int)response.StatusCode}",
        };
    }

    // See SshHandler.ResolveSecretsAsync — same contract: no marker, no lookup;
    // an unresolvable marker stays literal so the failure names it.
    private async Task<string?> ResolveSecretsAsync(string? value, CancellationToken ct)
        => string.IsNullOrEmpty(value) || !value.Contains("${secret:", StringComparison.Ordinal)
            ? value
            : await _secrets.SubstituteAsync(value, ct);

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };
}
