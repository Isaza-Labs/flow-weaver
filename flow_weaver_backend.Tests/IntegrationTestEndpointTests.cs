using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// The integration test bench: fire one action and reflect the full response
// back to the operator. Reflecting the response is exactly what makes this the
// most useful SSRF oracle in the codebase if left unguarded — so the URL guard
// and the header redaction are the two things that must never regress.
public class IntegrationTestEndpointTests
{

    private sealed class StubUrlGuard : IUrlGuard
    {
        private readonly string? _rejectWith;
        public List<(string Url, bool AllowPrivate)> Checked { get; } = new();
        public StubUrlGuard(string? rejectWith = null) => _rejectWith = rejectWith;

        public void EnsureSafe(string url, bool allowPrivate = false)
        {
            Checked.Add((url, allowPrivate));
            if (_rejectWith is not null) throw new InvalidOperationException(_rejectWith);
        }

        public void EnsureHostSafe(string host, bool allowPrivate = false)
        {
            CheckedHosts.Add((host, allowPrivate));
            if (_rejectWith is not null) throw new InvalidOperationException(_rejectWith);
        }

        public List<(string Host, bool AllowPrivate)> CheckedHosts { get; } = new();
    }

    // Replaces anything that looks like a bearer so the redaction is
    // observable without depending on the real pattern set.
    private sealed class StubRedactor : ISecretRedactor
    {
        public (string redacted, bool matched) Redact(string? input)
        {
            if (string.IsNullOrEmpty(input)) return (input ?? string.Empty, false);
            return input.Contains("secret-token", StringComparison.Ordinal)
                ? (input.Replace("secret-token", "[REDACTED:token]", StringComparison.Ordinal), true)
                : (input, false);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubUrlGuard Guard { get; set; } = new();
        public FakeHttpMessageHandler Handler { get; set; } =
            new(HttpStatusCode.OK, """{"count":3}""");

        public IntegrationTestController Build() => new(
            Db,
            new FakeUser(),
            new FakeHttpClientFactory(Handler),
            TestAuth.Applier(),
            new StubRedactor(),
            Guard)
        {
            ControllerContext = TestCtx.WithUser(),
        };

        public Guid SeedIntegration(
            string baseUrl = "https://netbox.example.com",
            string authConfig = "{}",
            bool allowPrivateNetwork = false,
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "netbox",
                BaseURL = baseUrl,
                AuthConfig = TestJson.Element(authConfig),
                Headers = TestJson.Element("{}"),
                AllowPrivateNetwork = allowPrivateNetwork,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedAction(
            Guid integrationId, string method = "GET", string path = "/api/dcim/devices",
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.IntegrationActions.Add(new IntegrationAction
            {
                IntegrationActionId = id,
                IntegrationId = integrationId,
                Name = "list_devices",
                Method = method,
                Path = path,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static IntegrationTestController.TestActionRequest Req(
        Dictionary<string, string>? prms = null,
        Dictionary<string, string>? query = null,
        string? body = null)
        => new()
        {
            Params = prms,
            Query = query,
            Body = body is null ? null : TestJson.Element(body),
        };

    // The endpoint returns an anonymous object; read it back through JSON.
    private static JsonElement Body(ActionResult<object> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return TestJson.Element(JsonSerializer.Serialize(ok.Value));
    }

    private static int StatusOf(ActionResult<object> result)
        => Assert.IsType<ObjectResult>(result.Result).StatusCode!.Value;

    // ─── resolution ─────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownActionIs404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().TestAction(Guid.NewGuid(), Req(), default)));
    }

    [Fact]
    public async Task InactiveActionIs404()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, active: false);

        Assert.Equal(404, StatusOf(await f.Build().TestAction(actionId, Req(), default)));
    }

    // An orphaned action names the missing parent rather than 500ing.
    [Fact]
    public async Task MissingParentIntegrationIs404()
    {
        using var f = new Fixture();
        var actionId = f.SeedAction(Guid.NewGuid());

        Assert.Equal(404, StatusOf(await f.Build().TestAction(actionId, Req(), default)));
    }

    [Fact]
    public async Task InactiveParentIntegrationIs404()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(active: false);
        var actionId = f.SeedAction(integrationId);

        Assert.Equal(404, StatusOf(await f.Build().TestAction(actionId, Req(), default)));
    }

    // A null body (the operator just hit "send") must not NRE.
    [Fact]
    public async Task ANullRequestBodyIsTreatedAsEmpty()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await f.Build().TestAction(actionId, null, default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ─── URL assembly ───────────────────────────────────────────────────

    [Fact]
    public async Task TheResolvedUrlIsEchoedBack()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(baseUrl: "https://netbox.example.com/");
        var actionId = f.SeedAction(integrationId, path: "/api/dcim/devices");

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        Assert.Equal("https://netbox.example.com/api/dcim/devices",
            body.GetProperty("resolved_url").GetString());
    }

    [Fact]
    public async Task PathParamsAreInterpolated()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, path: "/api/dcim/devices/{id}/");

        var body = Body(await f.Build().TestAction(
            actionId, Req(prms: new Dictionary<string, string> { ["id"] = "42" }), default));

        Assert.EndsWith("/api/dcim/devices/42/", body.GetProperty("resolved_url").GetString());
    }

    [Fact]
    public async Task QueryParamsAreAppendedAndEscaped()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(
            actionId, Req(query: new Dictionary<string, string> { ["name"] = "rtr 1" }), default));

        Assert.Contains("name=rtr%201", body.GetProperty("resolved_url").GetString());
    }

    // Empty query values are dropped so the URL doesn't gain `key=` noise.
    [Fact]
    public async Task EmptyQueryValuesAreDropped()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(
            actionId, Req(query: new Dictionary<string, string> { ["a"] = "" }), default));

        Assert.DoesNotContain("?", body.GetProperty("resolved_url").GetString());
    }

    // ─── the SSRF guard ─────────────────────────────────────────────────

    // Reflecting the response is what makes this endpoint dangerous, so the
    // guard sees the FINAL url including query.
    [Fact]
    public async Task TheFinalUrlIsChecked()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await f.Build().TestAction(
            actionId, Req(query: new Dictionary<string, string> { ["a"] = "b" }), default);

        Assert.Contains("a=b", Assert.Single(f.Guard.Checked).Url);
    }

    [Fact]
    public async Task ABlockedUrlIsRejectedBeforeAnyRequest()
    {
        using var f = new Fixture();
        f.Guard = new StubUrlGuard("blocked: loopback address");
        var integrationId = f.SeedIntegration(baseUrl: "http://127.0.0.1");
        var actionId = f.SeedAction(integrationId);

        var result = await f.Build().TestAction(actionId, Req(), default);

        Assert.Equal(400, StatusOf(result));
        Assert.Empty(f.Handler.Requests);
    }

    // The flag relaxes RFC-1918 only; loopback and metadata stay blocked by
    // the guard itself.
    [Fact]
    public async Task AllowPrivateNetworkIsForwarded()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(allowPrivateNetwork: true);
        var actionId = f.SeedAction(integrationId);

        await f.Build().TestAction(actionId, Req(), default);

        Assert.True(Assert.Single(f.Guard.Checked).AllowPrivate);
    }

    [Fact]
    public async Task PrivateNetworkIsNotAllowedByDefault()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await f.Build().TestAction(actionId, Req(), default);

        Assert.False(Assert.Single(f.Guard.Checked).AllowPrivate);
    }

    // ─── header redaction ───────────────────────────────────────────────

    // The operator needs to see WHAT would leave their network, but never the
    // raw credential — even on a test endpoint.
    [Fact]
    public async Task AppliedAuthHeadersAreEchoedRedacted()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(
            authConfig: """{"type":"bearer","token":"secret-token"}""");
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        var headers = body.GetProperty("applied_headers");
        var auth = headers.GetProperty("Authorization").GetString()!;
        Assert.Contains("[REDACTED:token]", auth);
        Assert.DoesNotContain("secret-token", auth);
    }

    // ...but the real credential still goes out on the wire.
    [Fact]
    public async Task TheRealCredentialIsStillSentUpstream()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(
            authConfig: """{"type":"bearer","token":"secret-token"}""");
        var actionId = f.SeedAction(integrationId);

        await f.Build().TestAction(actionId, Req(), default);

        Assert.Equal("secret-token", f.Handler.Requests[0].Headers.Authorization!.Parameter);
    }

    // ─── request shaping ────────────────────────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task TheActionsMethodIsUsed(string method)
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: method);

        await f.Build().TestAction(actionId, Req(), default);

        Assert.Equal(method, f.Handler.Requests[0].Method.Method);
    }

    [Fact]
    public async Task AnActionWithoutAMethodDefaultsToGet()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: "");

        await f.Build().TestAction(actionId, Req(), default);

        Assert.Equal(HttpMethod.Get, f.Handler.Requests[0].Method);
    }

    [Fact]
    public async Task TheSuppliedBodyIsSentAsJson()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: "POST");

        await f.Build().TestAction(actionId, Req(body: """{"name":"x"}"""), default);

        Assert.Equal("""{"name":"x"}""", Assert.Single(f.Handler.RequestBodies));
        Assert.Equal("application/json",
            f.Handler.Requests[0].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task NoBodyMeansNoContent()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await f.Build().TestAction(actionId, Req(), default);

        Assert.Null(f.Handler.Requests[0].Content);
    }

    // ─── the reflected response ─────────────────────────────────────────

    [Fact]
    public async Task TheFullResponseIsReflectedBack()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"count":3}""", Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("X-Total-Count", "3");
            return resp;
        });
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        var response = body.GetProperty("response");
        Assert.Equal(200, response.GetProperty("status_code").GetInt32());
        Assert.Contains("count", response.GetProperty("body").GetString());
        Assert.Equal("3", response.GetProperty("headers").GetProperty("X-Total-Count").GetString());
    }

    // An upstream error is still a successful TEST — the operator wants to see
    // the 404 the API returned, not a 404 from us.
    [Fact]
    public async Task AnUpstreamErrorIsReflectedNotRethrown()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.Forbidden, """{"detail":"no perms"}""");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await f.Build().TestAction(actionId, Req(), default);

        var body = Body(result);
        Assert.Equal(403, body.GetProperty("response").GetProperty("status_code").GetInt32());
        Assert.Contains("no perms", body.GetProperty("response").GetProperty("body").GetString());
    }

    // A transport failure is the most common thing an operator is debugging
    // here, so it comes back as a readable field rather than an exception.
    [Fact]
    public async Task ATransportFailureIsReportedAsAField()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("no such host"));
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        Assert.Contains("no such host", body.GetProperty("transport_error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("response").ValueKind);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsATransportError()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        Assert.Equal("request timed out", body.GetProperty("transport_error").GetString());
    }

    // The echo names which integration and action ran, so a bench result is
    // self-describing when pasted into a ticket.
    [Fact]
    public async Task TheEchoNamesTheIntegrationAndAction()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: "POST");

        var body = Body(await f.Build().TestAction(actionId, Req(), default));

        Assert.Equal("netbox", body.GetProperty("integration").GetString());
        Assert.Equal("list_devices", body.GetProperty("action").GetString());
        Assert.Equal("POST", body.GetProperty("method").GetString());
    }
}
