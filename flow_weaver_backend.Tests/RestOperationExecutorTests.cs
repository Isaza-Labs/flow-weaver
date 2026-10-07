using System.Net;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Reports;
using flow_weaver_backend.Services.Ai.RestExecutor;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// RestOperationExecutor is the agent's only path to the outside world: it
// turns an operationId + JSON fragments into a real HTTP request. These tests
// pin the parts that are security- or correctness-critical and unreachable
// through the handler tests: URL assembly, the SSRF-guard policy (on for
// global specs, off for admin-configured integrations), auth-scheme mapping,
// and the failure shapes the agent has to act on.
public class RestOperationExecutorTests
{
    private static readonly JsonElement None = default;

    // ─── doubles ────────────────────────────────────────────────────────

    // Serves whatever operations the test registers, keyed by id.
    private sealed class StubIndex : IApiSpecIndex
    {
        private readonly Dictionary<string, ApiOperation> _ops = new(StringComparer.Ordinal);

        public StubIndex Add(string operationId, string api, string method, string path)
        {
            _ops[operationId] = new ApiOperation
            {
                OperationId = operationId, Api = api, Method = method, Path = path,
            };
            return this;
        }

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => _ops.Values.ToList();
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null)
            => _ops.Values.ToList();
        public ApiOperation? GetByOperationId(string operationId)
            => _ops.TryGetValue(operationId, out var op) ? op : null;
    }

    // Rewrites ${secret:...} to a fixed value so the executor's substitution
    // points are observable without standing up the real resolver.
    private sealed class StubSecrets : ISecretResolver
    {
        private readonly string _value;
        public List<string> Substituted { get; } = new();

        public StubSecrets(string value = "RESOLVED") => _value = value;

        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct)
            => Task.FromResult<string?>(_value);

        public Task<string> SubstituteAsync(string template, CancellationToken ct)
        {
            Substituted.Add(template);
            return Task.FromResult(System.Text.RegularExpressions.Regex.Replace(
                template, @"\$\{secret:[^}]+\}", _value));
        }
    }

    // Passthrough by default; can be told to reject like the real resolver does
    // for an unknown report id.
    private sealed class StubReportRefs : IReportReferenceResolver
    {
        private readonly string[]? _unresolved;
        public StubReportRefs(string[]? unresolved = null) => _unresolved = unresolved;

        public Task<string> SubstituteAsync(string body, CancellationToken ct)
            => _unresolved is null
                ? Task.FromResult(body)
                : throw new ReportReferenceException(_unresolved);
    }

    private sealed class StubUrlGuard : IUrlGuard
    {
        private readonly string? _rejectWith;
        public List<string> Checked { get; } = new();
        public StubUrlGuard(string? rejectWith = null) => _rejectWith = rejectWith;

        public void EnsureSafe(string url, bool allowPrivate = false)
        {
            Checked.Add(url);
            if (_rejectWith is not null) throw new InvalidOperationException(_rejectWith);
        }

        // The REST executor only ever has URLs.
        public void EnsureHostSafe(string host, bool allowPrivate = false)
            => throw new InvalidOperationException("unexpected EnsureHostSafe for host " + host);
    }

    // ─── fixture ────────────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public StubIndex Index { get; } = new();
        public StubSecrets Secrets { get; set; } = new();
        public StubUrlGuard Guard { get; set; } = new();
        public StubReportRefs ReportRefs { get; set; } = new();
        public FakeHttpMessageHandler Handler { get; set; } =
            new(HttpStatusCode.OK, """{"ok":true}""");
        // Scripts the OAuth token endpoint for oauth2_client_credentials
        // integrations; untouched by every other auth method.
        public FakeHttpMessageHandler TokenHandler { get; set; } =
            new(HttpStatusCode.OK, """{"access_token":"oauth-token","expires_in":1800}""");
        public string? SelfBaseUrl { get; set; }

        public RestOperationExecutor Build() => new(
            new AiApiSpecRepository(Db),
            new IntegrationRepository(Db),
            Index,
            Secrets,
            ReportRefs,
            new FakeUser(),
            Guard,
            new FakeHttpClientFactory(Handler),
            TestAuth.Applier(TokenHandler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:SelfBaseUrl"] = SelfBaseUrl,
            }).Build(),
            NullLogger<RestOperationExecutor>.Instance);

        public void SeedSpec(string api, string yaml, Guid? integrationId = null, bool active = true)
        {
            Db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                Content = yaml,
                IntegrationId = integrationId,
                IsActive = active,
            });
            Db.SaveChanges();
        }

        public Guid SeedIntegration(
            string name = "netbox",
            string baseUrl = "https://netbox.internal",
            string authConfig = "{}",
            string headers = "{}")
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = name,
                BaseURL = baseUrl,
                AuthConfig = TestJson.Element(authConfig),
                Headers = TestJson.Element(headers),
            });
            Db.SaveChanges();
            return id;
        }

        public HttpRequestMessage SentRequest => Assert.Single(Handler.Requests);
        public void Dispose() => Db.Dispose();
    }

    private const string SimpleSpec = """
        servers:
          - url: https://api.example.com
        paths:
          /devices:
            get:
              operationId: list_devices
        """;

    // ─── resolution failures (each must be an actionable Error, not a throw) ──

    [Fact]
    public async Task UnknownOperation_FailsWithoutCallingOut()
    {
        using var f = new Fixture();

        var result = await f.Build().ExecuteAsync("nope", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // The index is a process-wide singleton; the spec itself lives in the
    // database. A stale index entry must not let an operation execute against
    // a spec row that is no longer there.
    [Fact]
    public async Task OperationInIndexButSpecRowMissing_Fails()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("is not available", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task InactiveSpecRow_Fails()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        f.SeedSpec("netbox", SimpleSpec, active: false);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Contains("not available", result.Error);
    }

    [Fact]
    public async Task SpecWithoutServerUrl_Fails()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        f.SeedSpec("netbox", "paths:\n  /devices:\n    get:\n      operationId: list_devices");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Contains("declares no server URL", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task IntegrationWithoutBaseUrl_FailsNamingTheIntegration()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        var integrationId = f.SeedIntegration(name: "netbox-prod", baseUrl: "");
        f.SeedSpec("netbox", "paths:\n  /devices:\n    get:\n      operationId: list_devices",
            integrationId: integrationId);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Contains("integration 'netbox-prod' has no base_url", result.Error);
    }

    [Fact]
    public async Task UnparseableSpec_FailsWithParseError()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        f.SeedSpec("netbox", "servers:\n  - [unclosed\n bad: : :");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Contains("spec parse failed", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // ─── URL assembly ───────────────────────────────────────────────────

    private static Fixture Ready(string spec = SimpleSpec, string method = "GET", string path = "/devices")
    {
        var f = new Fixture();
        f.Index.Add("list_devices", "netbox", method, path);
        f.SeedSpec("netbox", spec);
        return f;
    }

    [Fact]
    public async Task BuildsAbsoluteUrlFromServerAndPath()
    {
        using var f = Ready();

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(result.Success);
        Assert.Equal("https://api.example.com/devices", f.SentRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, f.SentRequest.Method);
    }

    // A trailing slash on the server URL must not produce a double slash.
    [Fact]
    public async Task TrailingSlashOnServerUrlIsNormalised()
    {
        using var f = Ready("""
            servers:
              - url: https://api.example.com/
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("https://api.example.com/devices", f.SentRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task PathParamsAreSubstitutedAndUrlEncoded()
    {
        using var f = Ready(path: "/devices/{id}", spec: """
            servers:
              - url: https://api.example.com
            paths:
              /devices/{id}:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync(
            "list_devices", TestJson.Element("""{"id":"a b/c"}"""), None, None, default);

        // AbsoluteUri, not ToString(): the latter decodes %20 back to a space.
        Assert.Equal("https://api.example.com/devices/a%20b%2Fc", f.SentRequest.RequestUri!.AbsoluteUri);
    }

    // Non-string path params are serialised as raw JSON so numbers work.
    [Fact]
    public async Task NonStringPathParamUsesRawJson()
    {
        using var f = Ready(path: "/devices/{id}", spec: """
            servers:
              - url: https://api.example.com
            paths:
              /devices/{id}:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync(
            "list_devices", TestJson.Element("""{"id":42}"""), None, None, default);

        Assert.EndsWith("/devices/42", f.SentRequest.RequestUri!.ToString());
    }

    // Forgetting path_params used to produce an opaque upstream 404; the
    // executor now names the missing placeholders so the agent can retry.
    [Fact]
    public async Task MissingPathParams_FailWithNamedPlaceholders()
    {
        using var f = Ready(path: "/devices/{id}/interfaces/{if_name}", spec: """
            servers:
              - url: https://api.example.com
            paths:
              /devices/{id}/interfaces/{if_name}:
                get:
                  operationId: list_devices
            """);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("'id'", result.Error);
        Assert.Contains("'if_name'", result.Error);
        Assert.Contains("path_params", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task PartiallyFilledPathParams_StillFail()
    {
        using var f = Ready(path: "/devices/{id}/interfaces/{if_name}", spec: """
            servers:
              - url: https://api.example.com
            paths:
              /devices/{id}/interfaces/{if_name}:
                get:
                  operationId: list_devices
            """);

        var result = await f.Build().ExecuteAsync(
            "list_devices", TestJson.Element("""{"id":"1"}"""), None, None, default);

        Assert.Contains("'if_name'", result.Error);
        Assert.DoesNotContain("'id'", result.Error);
    }

    [Fact]
    public async Task QueryParamsAreAppendedAndEncoded()
    {
        using var f = Ready();

        await f.Build().ExecuteAsync(
            "list_devices", None, TestJson.Element("""{"q":"core switch","limit":50}"""), None, default);

        var uri = f.SentRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("q=core%20switch", uri);
        Assert.Contains("limit=50", uri);
        Assert.StartsWith("https://api.example.com/devices?", uri);
    }

    // Null query values are dropped rather than sent as "key=" — the agent
    // routinely emits nulls for optional filters.
    [Fact]
    public async Task NullQueryParamsAreOmitted()
    {
        using var f = Ready();

        await f.Build().ExecuteAsync(
            "list_devices", None, TestJson.Element("""{"q":"x","site":null}"""), None, default);

        var uri = f.SentRequest.RequestUri!.ToString();
        Assert.Contains("q=x", uri);
        Assert.DoesNotContain("site", uri);
    }

    // KNOWN LIMITATION, pinned deliberately: CombineUrl appends the operation
    // path by string concatenation, so a `servers:` URL that already carries a
    // query string swallows the path into the query
    // (".../base?tenant=acme" + "/devices" → ".../base?tenant=acme/devices").
    // Query params are then appended with "&" because a '?' is already present.
    // OpenAPI server URLs virtually never carry a query, so this has not bitten
    // us; the test exists so a fix flips a red test rather than going unnoticed.
    [Fact]
    public async Task ServerUrlWithQueryString_SwallowsThePath()
    {
        using var f = Ready(spec: """
            servers:
              - url: https://api.example.com/base?tenant=acme
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync(
            "list_devices", None, TestJson.Element("""{"limit":5}"""), None, default);

        Assert.Equal("https://api.example.com/base?tenant=acme/devices&limit=5",
            f.SentRequest.RequestUri!.AbsoluteUri);
    }

    // ─── relative server URL (specs targeting this backend) ─────────────

    // A relative server URL means this process. The executor used to borrow
    // scheme+host from the incoming request; behind the frontend proxy in
    // Docker that Host is the browser-facing `localhost:3000`, which the
    // backend then dialled from inside its own container. It no longer takes
    // the request at all: web chat and background runs resolve identically.
    [Fact]
    public async Task RelativeServerUrl_ConfiguredSelfBaseUrl_WinsRegardlessOfCaller()
    {
        using var f = Ready(spec: """
            servers:
              - url: /api
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);
        f.SelfBaseUrl = "http://127.0.0.1:8080";

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("http://127.0.0.1:8080/api/devices", f.SentRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RelativeServerUrl_NoRequest_FallsBackToSelfBaseUrl()
    {
        using var f = Ready(spec: """
            servers:
              - url: /api
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);
        f.SelfBaseUrl = "http://backend:9000/";

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("http://backend:9000/api/devices", f.SentRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RelativeServerUrl_NoConfiguredSelfBaseUrl_UsesLocalhostDefault()
    {
        using var f = Ready(spec: """
            servers:
              - url: /api
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("http://localhost:8080/api/devices", f.SentRequest.RequestUri!.ToString());
    }

    // ─── SSRF guard policy ──────────────────────────────────────────────

    // Global spec with a literal server URL: the agent could be steered here,
    // so the guard runs.
    [Fact]
    public async Task GlobalSpec_IsCheckedByTheUrlGuard()
    {
        using var f = Ready();

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("https://api.example.com/devices", Assert.Single(f.Guard.Checked));
    }

    [Fact]
    public async Task UrlGuardRejection_FailsBeforeAnyRequest()
    {
        using var f = Ready();
        f.Guard = new StubUrlGuard("blocked: link-local address");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("blocked: link-local", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // Integration-scoped: an Operator configured this BaseURL deliberately and
    // on-prem tools legitimately live on private ranges, so the guard is off.
    [Fact]
    public async Task IntegrationScopedSpec_SkipsTheUrlGuard()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        var integrationId = f.SeedIntegration(baseUrl: "http://10.0.0.5:8000");
        f.SeedSpec("netbox", SimpleSpec, integrationId: integrationId);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(result.Success);
        Assert.Empty(f.Guard.Checked);
        Assert.Equal("http://10.0.0.5:8000/devices", f.SentRequest.RequestUri!.ToString());
    }

    // Self-calls are trivially safe and must not be blocked by the guard.
    [Fact]
    public async Task RelativeServerUrl_SkipsTheUrlGuard()
    {
        using var f = Ready(spec: """
            servers:
              - url: /api
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Empty(f.Guard.Checked);
    }

    // The integration's BaseURL is authoritative — it must win over the spec's
    // own `servers:` entry.
    [Fact]
    public async Task IntegrationBaseUrl_OverridesSpecServerUrl()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        var integrationId = f.SeedIntegration(baseUrl: "https://netbox.internal");
        f.SeedSpec("netbox", SimpleSpec, integrationId: integrationId);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.StartsWith("https://netbox.internal/", f.SentRequest.RequestUri!.ToString());
    }

    // ─── request body ───────────────────────────────────────────────────

    private const string PostSpec = """
        servers:
          - url: https://api.example.com
        paths:
          /devices:
            post:
              operationId: list_devices
              requestBody:
                content:
                  application/json:
                    schema:
                      type: object
        """;

    [Fact]
    public async Task JsonBodyIsSentWhenSpecDeclaresOne()
    {
        using var f = Ready(PostSpec, method: "POST");

        await f.Build().ExecuteAsync(
            "list_devices", None, None, TestJson.Element("""{"name":"sw1"}"""), default);

        Assert.Equal("""{"name":"sw1"}""", Assert.Single(f.Handler.RequestBodies));
        Assert.Equal("application/json", f.SentRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task JsonArrayBodyIsSent()
    {
        using var f = Ready(PostSpec, method: "POST");

        await f.Build().ExecuteAsync(
            "list_devices", None, None, TestJson.Element("""[{"name":"sw1"}]"""), default);

        Assert.Equal("""[{"name":"sw1"}]""", Assert.Single(f.Handler.RequestBodies));
    }

    // A spec with no requestBody must not get one even if the agent supplies it.
    [Fact]
    public async Task BodyIsDroppedWhenSpecDeclaresNoRequestBody()
    {
        using var f = Ready();

        await f.Build().ExecuteAsync(
            "list_devices", None, None, TestJson.Element("""{"name":"sw1"}"""), default);

        Assert.Null(f.SentRequest.Content);
    }

    [Fact]
    public async Task NonObjectBodyIsNotSent()
    {
        using var f = Ready(PostSpec, method: "POST");

        await f.Build().ExecuteAsync(
            "list_devices", None, None, TestJson.Element("\"just a string\""), default);

        Assert.Null(f.SentRequest.Content);
    }

    [Fact]
    public async Task SecretMarkersInBodyAreSubstitutedBeforeSending()
    {
        using var f = Ready(PostSpec, method: "POST");

        await f.Build().ExecuteAsync(
            "list_devices", None, None,
            TestJson.Element("""{"token":"${secret:secret:api-token:value}"}"""), default);

        Assert.Equal("""{"token":"RESOLVED"}""", Assert.Single(f.Handler.RequestBodies));
    }

    // A broken ${report:...} reference is a hard failure — shipping the literal
    // marker would attach a corrupt file.
    [Fact]
    public async Task UnresolvableReportReference_FailsBeforeSending()
    {
        using var f = Ready(PostSpec, method: "POST");
        f.ReportRefs = new StubReportRefs(new[] { "abc123" });

        var result = await f.Build().ExecuteAsync(
            "list_devices", None, None, TestJson.Element("""{"attachment":"${report:abc123}"}"""), default);

        Assert.False(result.Success);
        Assert.Contains("abc123", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // ─── auth schemes (global spec path) ────────────────────────────────

    private static string SpecWithScheme(string schemeYaml, string securityName = "main") => $"""
        servers:
          - url: https://api.example.com
        components:
          securitySchemes:
        {schemeYaml}
        security:
          - {securityName}: []
        paths:
          /devices:
            get:
              operationId: list_devices
        """;

    [Fact]
    public async Task HttpBearerSchemeSetsAuthorizationHeader()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: http
                  scheme: bearer
                  x-credential-ref: ${secret:secret:api-token:value}
            """));

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("Bearer", f.SentRequest.Headers.Authorization!.Scheme);
        Assert.Equal("RESOLVED", f.SentRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task HttpBasicSchemeBase64EncodesTheResolvedValue()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: http
                  scheme: basic
                  x-credential-ref: ${secret:credential:core:password}
            """));
        f.Secrets = new StubSecrets("user:pass");

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        var auth = f.SentRequest.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("user:pass",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }

    [Fact]
    public async Task ApiKeyInHeaderUsesDeclaredParamName()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: apiKey
                  in: header
                  name: X-Custom-Key
                  x-credential-ref: ${secret:secret:k:value}
            """));

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("RESOLVED", Assert.Single(f.SentRequest.Headers.GetValues("X-Custom-Key")));
    }

    [Fact]
    public async Task ApiKeyInHeaderDefaultsToXApiKey()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: apiKey
                  in: header
                  x-credential-ref: ${secret:secret:k:value}
            """));

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("RESOLVED", Assert.Single(f.SentRequest.Headers.GetValues("X-API-Key")));
    }

    [Fact]
    public async Task ApiKeyInQueryIsAppendedToTheUrl()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: apiKey
                  in: query
                  name: api_key
                  x-credential-ref: ${secret:secret:k:value}
            """));

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("https://api.example.com/devices?api_key=RESOLVED",
            f.SentRequest.RequestUri!.ToString());
    }

    // Appending the key must not clobber an existing query string.
    [Fact]
    public async Task ApiKeyInQueryUsesAmpersandWhenQueryAlreadyPresent()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: apiKey
                  in: query
                  name: api_key
                  x-credential-ref: ${secret:secret:k:value}
            """));

        await f.Build().ExecuteAsync(
            "list_devices", None, TestJson.Element("""{"limit":5}"""), None, default);

        var uri = f.SentRequest.RequestUri!.ToString();
        Assert.Contains("limit=5&api_key=RESOLVED", uri);
    }

    // A scheme with no x-credential-ref means anonymous — no headers touched.
    [Fact]
    public async Task SchemeWithoutCredentialRef_SendsAnonymously()
    {
        using var f = Ready(SpecWithScheme("""
                main:
                  type: http
                  scheme: bearer
            """));

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Null(f.SentRequest.Headers.Authorization);
    }

    // A `security:` entry naming a scheme that isn't declared must be skipped,
    // not throw.
    [Fact]
    public async Task SecurityRefToUndeclaredScheme_IsIgnored()
    {
        using var f = Ready("""
            servers:
              - url: https://api.example.com
            security:
              - ghost: []
            paths:
              /devices:
                get:
                  operationId: list_devices
            """);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(result.Success);
        Assert.Null(f.SentRequest.Headers.Authorization);
    }

    // Operation-level security overrides the global default.
    [Fact]
    public async Task OperationLevelSecurityOverridesGlobal()
    {
        using var f = Ready("""
            servers:
              - url: https://api.example.com
            components:
              securitySchemes:
                globalScheme:
                  type: apiKey
                  in: header
                  name: X-Global
                  x-credential-ref: ${secret:secret:g:value}
                opScheme:
                  type: apiKey
                  in: header
                  name: X-Op
                  x-credential-ref: ${secret:secret:o:value}
            security:
              - globalScheme: []
            paths:
              /devices:
                get:
                  operationId: list_devices
                  security:
                    - opScheme: []
            """);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(f.SentRequest.Headers.Contains("X-Op"));
        Assert.False(f.SentRequest.Headers.Contains("X-Global"));
    }

    // ─── integration auth wins over spec security ───────────────────────

    [Fact]
    public async Task IntegrationAuthIsAppliedInsteadOfSpecSecurity()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        var integrationId = f.SeedIntegration(
            authConfig: """{"type":"bearer","token":"integration-token"}""",
            headers: """{"X-Tenant":"acme"}""");
        f.SeedSpec("netbox", SpecWithScheme("""
                main:
                  type: apiKey
                  in: header
                  name: X-Spec-Key
                  x-credential-ref: ${secret:secret:k:value}
            """), integrationId: integrationId);

        await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(f.SentRequest.Headers.Contains("X-Spec-Key"));
        Assert.Equal("acme", Assert.Single(f.SentRequest.Headers.GetValues("X-Tenant")));
    }

    // Regression: the executor used the sync builder, which skips
    // oauth2_client_credentials, so the call went out anonymous and the
    // upstream answered 401 while the integration health check was green.
    [Fact]
    public async Task OAuthClientCredentialsIntegration_SendsBearerFromTokenEndpoint()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        var integrationId = f.SeedIntegration(authConfig: """
            {"method":"oauth2_client_credentials","token_url":"https://idp.example.com/token","client_id":"cid","client_secret":"cs"}
            """);
        f.SeedSpec("netbox", SimpleSpec, integrationId: integrationId);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(result.Success);
        Assert.Single(f.TokenHandler.Requests);
        Assert.Equal("Bearer", f.SentRequest.Headers.Authorization?.Scheme);
        Assert.Equal("oauth-token", f.SentRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task OAuthTokenGrantFailure_FailsWithoutCallingUpstream()
    {
        using var f = new Fixture();
        f.Index.Add("list_devices", "netbox", "GET", "/devices");
        f.TokenHandler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var integrationId = f.SeedIntegration(name: "servicenow", authConfig: """
            {"method":"oauth2_client_credentials","token_url":"https://idp.example.com/token","client_id":"cid","client_secret":"bad"}
            """);
        f.SeedSpec("netbox", SimpleSpec, integrationId: integrationId);

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("servicenow", result.Error);
        Assert.Contains("auth failed", result.Error);
        Assert.Empty(f.Handler.Requests);
    }

    // ─── response shaping ───────────────────────────────────────────────

    [Fact]
    public async Task JsonResponseIsReturnedParsed()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"count":3}""");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, result.Body.GetProperty("count").GetInt32());
    }

    // Non-JSON bodies are wrapped as {"raw": "..."} so the agent always gets
    // a JSON value back instead of a parse error.
    [Fact]
    public async Task NonJsonResponseIsWrappedAsRaw()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "plain text", "text/plain");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("plain text", result.Body.GetProperty("raw").GetString());
    }

    [Fact]
    public async Task EmptyResponseBodyBecomesJsonNull()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.NoContent, "");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal(JsonValueKind.Null, result.Body.ValueKind);
    }

    // An upstream error is a completed call, not an executor failure: the agent
    // needs the status + body to decide what to do next.
    [Fact]
    public async Task UpstreamErrorStatusIsReportedNotThrown()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, """{"detail":"gone"}""");

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);   // 404 is not a success status
        Assert.Null(result.Error);      // ...but it is not an executor error either
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("gone", result.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ResponseHeadersAreCollectedCaseInsensitively()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.Add("X-Request-Id", "req-1");
            return resp;
        });

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.Equal("req-1", result.Headers["x-request-id"]);
        Assert.True(result.Headers.ContainsKey("Content-Type"));
    }

    [Fact]
    public async Task TransportFailureIsReportedAsHttpError()
    {
        using var f = Ready();
        f.Handler = new FakeHttpMessageHandler(
            _ => throw new HttpRequestException("connection refused"));

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, default);

        Assert.False(result.Success);
        Assert.Contains("http error", result.Error);
        Assert.Contains("connection refused", result.Error);
        Assert.Equal(0, result.StatusCode);
    }

    // A caller-cancelled token surfaces as the timeout failure shape because
    // the executor links it to its own 15s budget.
    [Fact]
    public async Task CancelledCallReturnsFailureNotUnhandledException()
    {
        using var f = Ready();
        using var cts = new CancellationTokenSource();
        f.Handler = new FakeHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException();
        });

        var result = await f.Build().ExecuteAsync("list_devices", None, None, None, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error);
    }
}
