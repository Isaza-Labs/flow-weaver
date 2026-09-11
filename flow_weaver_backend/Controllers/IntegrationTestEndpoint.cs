using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// FR-015 / FR-016: operator's test bench for a registered integration
// action. Fires the same HTTP call that an integration_action workflow
// node would, but returns a detailed transcript instead of persisting a
// step_run — ideal for validating a spec after upload, or confirming an
// auth_config works before wiring it into a DAG.
//
// Kept as a standalone controller (not folded into IntegrationController)
// because the endpoint takes the ACTION id as the path segment, not the
// integration id, which would clash with the CRUD routes otherwise.
[ApiController]
[Route("api/integration-test")]
[HasPermission("integration.test")]
public class IntegrationTestController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IIntegrationAuthApplier _auth;
    private readonly ISecretRedactor _redactor;
    private readonly IUrlGuard _urlGuard;

    public IntegrationTestController(
        AppDbContext db,
        ICurrentUser caller,
        IHttpClientFactory httpFactory,
        IIntegrationAuthApplier auth,
        ISecretRedactor redactor,
        IUrlGuard urlGuard)
    {
        _db = db;
        _caller = caller;
        _httpFactory = httpFactory;
        _auth = auth;
        _redactor = redactor;
        _urlGuard = urlGuard;
    }

    public sealed class TestActionRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("body")]
        public JsonElement? Body { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public Dictionary<string, string>? Params { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("query")]
        public Dictionary<string, string>? Query { get; set; }
    }

    [HttpPost("action/{actionId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<object>> TestAction(
        Guid actionId, [FromBody] TestActionRequest? request, CancellationToken ct)
    {
        request ??= new TestActionRequest();

        var action = await _db.IntegrationActions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.IntegrationActionId == actionId
                && a.IsActive, ct);
        if (action is null)
            return Problems.NotFound("integration action", actionId);

        var integration = await _db.Integrations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.IntegrationId == action.IntegrationId
                && i.IsActive, ct);
        if (integration is null)
            return Problems.NotFound("parent integration", action.IntegrationId);

        // Template `{p}` path interpolation — mirrors IntegrationActionHandler.
        var path = action.Path;
        if (request.Params is not null)
            foreach (var (k, v) in request.Params)
                path = path.Replace("{" + k + "}", v);

        var url = integration.BaseURL.TrimEnd('/') + "/" + path.TrimStart('/');
        if (request.Query is { Count: > 0 })
        {
            var qs = string.Join("&", request.Query
                .Where(q => !string.IsNullOrEmpty(q.Value))
                .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
            if (!string.IsNullOrEmpty(qs))
                url += (url.Contains('?') ? "&" : "?") + qs;
        }

        // SSRF guard. The test bench reflects the response back to the
        // caller, which makes it the most useful SSRF oracle in the
        // codebase if left unguarded — block private/loopback/metadata
        // targets explicitly. Mirror the runtime handler: respect the
        // integration's AllowPrivateNetwork flag for RFC-1918 targets,
        // keep loopback + metadata IPs blocked unconditionally.
        try
        {
            _urlGuard.EnsureSafe(url, allowPrivate: integration.AllowPrivateNetwork);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.BadRequest(ex.Message, code: "url_blocked");
        }

        var method = new HttpMethod(string.IsNullOrWhiteSpace(action.Method) ? "GET" : action.Method);
        using var msg = new HttpRequestMessage(method, url);
        // Async so an oauth2_client_credentials integration exercises its real
        // token grant — exactly what the test bench exists to validate.
        try
        {
            await _auth.ApplyAsync(msg, integration, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.BadRequest($"integration auth failed: {ex.Message}", code: "auth_failed");
        }
        if (request.Body is { } body && body.ValueKind != JsonValueKind.Undefined)
            msg.Content = new StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json");

        // Capture applied headers BEFORE send so the operator sees what
        // would leave their network. Redacted so we don't return the
        // raw token even on a test endpoint.
        var appliedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in msg.Headers)
        {
            var joined = string.Join(", ", h.Value);
            appliedHeaders[h.Key] = _redactor.Redact(joined).redacted;
        }

        // Same private-network policy on redirect targets as on the URL the
        // guard already cleared above.
        msg.WithPrivateNetworkPolicy(integration.AllowPrivateNetwork);

        var clientName = integration.TLSSkipVerify
            ? IntegrationHealthChecker.InsecureClientName
            : IntegrationHealthChecker.ClientName;
        var client = _httpFactory.CreateClient(clientName);

        HttpResponseMessage? response = null;
        string? transportError = null;
        try
        {
            response = await client.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            transportError = ex.Message;
        }
        catch (TaskCanceledException)
        {
            transportError = "request timed out";
        }
        // Always release the underlying socket back to the pool, even
        // if the response body parser throws below.
        using var _responseScope = response;

        object result;
        if (response is null)
        {
            result = new
            {
                integration = integration.Name,
                action = action.Name,
                method = action.Method,
                resolved_url = url,
                applied_headers = appliedHeaders,
                transport_error = transportError,
                response = (object?)null,
            };
        }
        else
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) responseHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers) responseHeaders[h.Key] = string.Join(", ", h.Value);
            var statusCode = (int)response.StatusCode;
            response.Dispose();

            result = new
            {
                integration = integration.Name,
                action = action.Name,
                method = action.Method,
                resolved_url = url,
                applied_headers = appliedHeaders,
                response = new
                {
                    status_code = statusCode,
                    headers = responseHeaders,
                    body = responseBody,
                },
            };
        }

        return Ok(result);
    }
}
