using System.Net;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// The workflow node that calls a registered integration. Three things here are
// load-bearing: the SSRF guard runs on the FINAL url (an admin-registered
// BaseURL is not automatically trusted), a needs_config integration is stopped
// with an actionable message instead of a confusing upstream 401, and the JSON
// response is exposed as `data` alongside the raw `body` — the missing `data`
// is what used to break every agent-generated template.
public class IntegrationActionHandlerTests
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

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubUrlGuard Guard { get; set; } = new();
        public FakeHttpMessageHandler Handler { get; set; } =
            new(HttpStatusCode.OK, """{"results":[{"name":"rtr-1"}]}""");

        public IntegrationActionHandler Build() => new(
            new IntegrationRepository(Db),
            new IntegrationActionRepository(Db),
            new FakeHttpClientFactory(Handler),
            TestAuth.Applier(),
            Guard,
            NullLogger<IntegrationActionHandler>.Instance);

        public Guid SeedIntegration(
            string baseUrl = "https://netbox.example.com",
            string status = "ready",
            bool allowPrivateNetwork = false,
            string authConfig = "{}",
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "netbox",
                BaseURL = baseUrl,
                Status = status,
                AllowPrivateNetwork = allowPrivateNetwork,
                AuthConfig = TestJson.Element(authConfig),
                Headers = TestJson.Element("{}"),
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
                Enabled = true,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static SnippetRequest Request(string input)
        => new()
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "n1",
            SnippetId = null,
            SnippetType = "integration_action",
            InputPayload = TestJson.Element(input),
            DeviceId = null,
        };

    private static string Input(Guid integrationId, Guid actionId, string extra = "")
        => "{\"integration_id\":\"" + integrationId + "\",\"action_id\":\"" + actionId + "\""
           + (extra.Length > 0 ? "," + extra : "") + "}";

    private static async Task<SnippetResult> Run(Fixture f, string input)
        => await f.Build().ExecuteAsync(Request(input), default);

    // ─── id validation ──────────────────────────────────────────────────

    // The two failure modes are deliberately distinct: conflating "missing"
    // with "bogus value" made the agent reshape the payload forever instead of
    // resolving the real id.
    [Theory]
    [InlineData("integration_id")]
    [InlineData("action_id")]
    public async Task MissingIdSaysItIsRequired(string key)
    {
        using var f = new Fixture();
        var other = key == "integration_id" ? "action_id" : "integration_id";
        var input = "{\"" + other + "\":\"" + Guid.NewGuid() + "\"}";

        var result = await Run(f, input);

        Assert.False(result.Success);
        Assert.Equal($"input.{key} is required", result.Error);
    }

    [Fact]
    public async Task NullIdIsTreatedAsMissing()
    {
        using var f = new Fixture();

        var result = await Run(f, """{"integration_id":null,"action_id":null}""");

        Assert.Equal("input.integration_id is required", result.Error);
    }

    // A human-readable name instead of a GUID must say so, and point at the
    // tool that resolves it.
    [Fact]
    public async Task NonGuidIdNamesTheOffendingValue()
    {
        using var f = new Fixture();

        var result = await Run(f, """{"integration_id":"netbox","action_id":"list_devices"}""");

        Assert.Contains("must be a GUID", result.Error);
        Assert.Contains("netbox", result.Error);
        Assert.Contains("list_palette_actions", result.Error);
    }

    // ─── resolution + readiness ─────────────────────────────────────────

    [Fact]
    public async Task UnknownIntegrationFails()
    {
        using var f = new Fixture();

        var result = await Run(f, Input(Guid.NewGuid(), Guid.NewGuid()));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task UnknownActionFails()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();

        var result = await Run(f, Input(integrationId, Guid.NewGuid()));

        Assert.Contains("integration action", result.Error);
        Assert.Contains("not found", result.Error);
    }

    // Right after the import wizard auto-creates an integration it has no
    // credentials; a clear message beats a 401 or a TLS handshake failure.
    [Fact]
    public async Task NeedsConfigIntegrationIsStoppedWithAnActionableMessage()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(status: IntegrationStatus.NeedsConfig);
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.False(result.Success);
        Assert.Contains("needs configuration", result.Error);
        Assert.Contains($"/integrations/{integrationId}", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // ─── URL assembly ───────────────────────────────────────────────────

    [Fact]
    public async Task BuildsTheUrlFromBaseAndPath()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(baseUrl: "https://netbox.example.com/");
        var actionId = f.SeedAction(integrationId, path: "/api/dcim/devices");

        await Run(f, Input(integrationId, actionId));

        Assert.Equal("https://netbox.example.com/api/dcim/devices",
            Assert.Single(f.Handler.Requests).RequestUri!.ToString());
    }

    // Path placeholders come from `params` — the bucket the import rewriter
    // fills.
    [Fact]
    public async Task PathPlaceholdersAreInterpolatedFromParams()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, path: "/api/dcim/devices/{id}/");

        await Run(f, Input(integrationId, actionId, "\"params\":{\"id\":\"42\"}"));

        Assert.EndsWith("/api/dcim/devices/42/", f.Handler.Requests[0].RequestUri!.ToString());
    }

    // An unfilled placeholder is left literal rather than silently dropped —
    // the upstream 404 then names it.
    [Fact]
    public async Task UnfilledPlaceholderIsLeftLiteral()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, path: "/api/dcim/devices/{id}/");

        await Run(f, Input(integrationId, actionId));

        Assert.Contains("%7Bid%7D", f.Handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task QueryParamsAreAppendedAndEscaped()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId, "\"query\":{\"name\":\"rtr 1\",\"site\":\"madrid\"}"));

        var uri = f.Handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("name=rtr%201", uri);
        Assert.Contains("site=madrid", uri);
    }

    // Non-string query values are skipped rather than stringified oddly.
    [Fact]
    public async Task NonStringQueryValuesAreSkipped()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId, "\"query\":{\"limit\":50,\"name\":\"x\"}"));

        var uri = f.Handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("name=x", uri);
        Assert.DoesNotContain("limit", uri);
    }

    // ─── the SSRF guard ─────────────────────────────────────────────────

    // The guard runs on the FINAL url — an admin-registered BaseURL is not
    // automatically trusted.
    [Fact]
    public async Task TheFinalUrlIsCheckedByTheGuard()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId, "\"query\":{\"a\":\"b\"}"));

        var (url, _) = Assert.Single(f.Guard.Checked);
        Assert.Contains("a=b", url);   // query included, i.e. the final url
    }

    [Fact]
    public async Task GuardRejectionStopsTheCallBeforeAnyRequest()
    {
        using var f = new Fixture();
        f.Guard = new StubUrlGuard("blocked: cloud metadata address");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.False(result.Success);
        Assert.Contains("cloud metadata", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // Self-hosted installs legitimately run NetBox on an RFC-1918 address, so
    // the flag relaxes ONLY the private-range check.
    [Fact]
    public async Task AllowPrivateNetworkIsForwardedToTheGuard()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(allowPrivateNetwork: true);
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId));

        Assert.True(Assert.Single(f.Guard.Checked).AllowPrivate);
    }

    [Fact]
    public async Task PrivateNetworkIsNotAllowedByDefault()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(allowPrivateNetwork: false);
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId));

        Assert.False(Assert.Single(f.Guard.Checked).AllowPrivate);
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

        await Run(f, Input(integrationId, actionId));

        Assert.Equal(method, f.Handler.Requests[0].Method.Method);
    }

    [Fact]
    public async Task AnActionWithoutAMethodDefaultsToGet()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: "");

        await Run(f, Input(integrationId, actionId));

        Assert.Equal(HttpMethod.Get, f.Handler.Requests[0].Method);
    }

    [Fact]
    public async Task TheBodyIsSentAsJson()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId, method: "POST");

        await Run(f, Input(integrationId, actionId, "\"body\":{\"name\":\"new-device\"}"));

        Assert.Equal("""{"name":"new-device"}""", Assert.Single(f.Handler.RequestBodies));
        Assert.Equal("application/json", f.Handler.Requests[0].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task NoBodyMeansNoContent()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId));

        Assert.Null(f.Handler.Requests[0].Content);
    }

    // The integration's configured auth is applied by the shared builder.
    [Fact]
    public async Task IntegrationAuthIsApplied()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration(
            authConfig: """{"type":"bearer","token":"secret-token"}""");
        var actionId = f.SeedAction(integrationId);

        await Run(f, Input(integrationId, actionId));

        Assert.Equal("secret-token", f.Handler.Requests[0].Headers.Authorization!.Parameter);
    }

    // ─── response shaping ───────────────────────────────────────────────

    // `data` is the parsed view; without it a template like
    // `{{ steps.X.output.data.results[0].name }}` cannot work, and `body`
    // alone is a string every agent-generated workflow tripped over.
    [Fact]
    public async Task JsonResponsesAreExposedAsBothRawBodyAndParsedData()
    {
        using var f = new Fixture();
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.True(result.Success);
        Assert.Equal(200, result.Output.GetProperty("status_code").GetInt32());
        Assert.Contains("rtr-1", result.Output.GetProperty("body").GetString());
        Assert.Equal("rtr-1",
            result.Output.GetProperty("data").GetProperty("results")[0].GetProperty("name").GetString());
    }

    // Vendors use their own JSON flavours; the content-type match is lenient.
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/hal+json")]
    [InlineData("application/vnd.netbox+json")]
    public async Task JsonFlavoursAreAllParsed(string contentType)
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"ok":true}""", contentType);
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.True(result.Output.GetProperty("data").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task NonJsonResponsesLeaveDataNull()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "plain text", "text/plain");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.Equal(JsonValueKind.Null, result.Output.GetProperty("data").ValueKind);
        Assert.Equal("plain text", result.Output.GetProperty("body").GetString());
    }

    // A server that lies about its content-type must not fail the step.
    [Fact]
    public async Task AMislabelledBodyIsNonFatal()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "<html>not json</html>", "application/json");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.True(result.Success);
        Assert.Equal(JsonValueKind.Null, result.Output.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ResponseHeadersAreExposed()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("X-Total-Count", "42");
            return resp;
        });
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.Equal("42", result.Output.GetProperty("headers").GetProperty("X-Total-Count").GetString());
    }

    // An upstream error status fails the STEP but still returns the body, so
    // the workflow author can see what the API said.
    [Fact]
    public async Task UpstreamErrorFailsTheStepButKeepsTheBody()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, """{"detail":"Not found."}""");
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.False(result.Success);
        Assert.Equal("HTTP 404", result.Error);
        Assert.Equal(404, result.Output.GetProperty("status_code").GetInt32());
        Assert.Equal("Not found.", result.Output.GetProperty("data").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TransportFailureIsAStepFailure()
    {
        using var f = new Fixture();
        f.Handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var integrationId = f.SeedIntegration();
        var actionId = f.SeedAction(integrationId);

        var result = await Run(f, Input(integrationId, actionId));

        Assert.False(result.Success);
        Assert.Contains("integration request failed", result.Error);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public void HandlerDeclaresItsType()
    {
        using var f = new Fixture();

        Assert.Equal("integration_action", f.Build().Type);
    }
}
