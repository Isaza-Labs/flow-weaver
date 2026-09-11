using System.Net;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Permission;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

public class IntegrationServiceTests
{
    private readonly FakeUser _caller = new();

    private IntegrationService NewSvc(AppDbContext db, FakeOAuthTokens? tokens = null)
        => new(new IntegrationRepository(db), _caller, new FakeAudit(), new FakeTrace(),
               new ResourcePermissionService(new ResourcePermissionRepository(db), new UserRepository(db), _caller, new FakeAudit(), NullLogger<ResourcePermissionService>.Instance),
               new FakeAppSettings(), tokens ?? new FakeOAuthTokens(), NullLogger<IntegrationService>.Instance);

    private static CreateIntegration Sample(string name = "netbox")
        => new() { Name = name, Type = "rest", BaseURL = "https://nb.example" };

    private static Guid CreatedId(ActionResult<IntegrationResponse> r)
        => ((IntegrationResponse)((CreatedAtActionResult)r.Result!).Value!).IntegrationId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db).PostAsync(Sample());
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.IsType<IntegrationResponse>(created.Value);
        Assert.Single(db.Set<IntegrationModel>());
    }

    [Theory]
    [InlineData("", "rest", "https://x")]
    [InlineData("n", "", "https://x")]
    [InlineData("n", "rest", "")]
    public async Task Post_rejects_missing_required(string name, string type, string baseUrl)
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db).PostAsync(new CreateIntegration { Name = name, Type = type, BaseURL = baseUrl });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Get_lists_and_delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db);
        var id = CreatedId(await svc.PostAsync(Sample()));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(1, Assert.IsType<ListResponse<IntegrationResponse>>(ok.Value).Total);

        await svc.DeleteAsync(id);
        Assert.False(db.Set<IntegrationModel>().Single().IsActive);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db).UpdateAsync(Guid.NewGuid(), new UpdateIntegration())).Result);
    }

    // Rotating credentials must take effect immediately: an auth_config
    // update invalidates the cached OAuth access token; unrelated updates
    // leave the cache alone (a valid token keeps serving).
    [Fact]
    public async Task Update_auth_config_invalidates_cached_oauth_token()
    {
        using var db = TestDb.NewContext();
        var tokens = new FakeOAuthTokens();
        var svc = NewSvc(db, tokens);
        var id = CreatedId(await svc.PostAsync(Sample()));

        await svc.UpdateAsync(id, new UpdateIntegration
        {
            AuthConfig = TestJson.Element("""{"method":"bearer","token":"rotated"}"""),
        });
        Assert.Contains(id, tokens.Invalidated);

        tokens.Invalidated.Clear();
        await svc.UpdateAsync(id, new UpdateIntegration { Name = "renamed" });
        Assert.Empty(tokens.Invalidated);
    }
}

public class IntegrationAuthBuilderTests
{
    [Fact]
    public void Apply_sets_custom_headers_and_bearer_auth()
    {
        var builder = new IntegrationAuthBuilder(NullLogger<IntegrationAuthBuilder>.Instance);
        var integration = new IntegrationModel
        {
            IntegrationId = Guid.NewGuid(),
            Headers = TestJson.Element("""{"X-Env":"prod"}"""),
            AuthConfig = TestJson.Element("""{"method":"bearer","token":"abc123"}"""),
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "https://nb.example/api");

        builder.Apply(request, integration);

        Assert.True(request.Headers.Contains("X-Env"));
        Assert.NotNull(request.Headers.Authorization);
    }

    [Fact]
    public void Apply_basic_auth_sets_authorization()
    {
        var builder = new IntegrationAuthBuilder(NullLogger<IntegrationAuthBuilder>.Instance);
        var integration = new IntegrationModel
        {
            IntegrationId = Guid.NewGuid(),
            AuthConfig = TestJson.Element("""{"method":"basic","username":"u","password":"p"}"""),
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "https://nb.example");
        builder.Apply(request, integration);
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public void Apply_api_key_sets_header()
    {
        var builder = new IntegrationAuthBuilder(NullLogger<IntegrationAuthBuilder>.Instance);
        var integration = new IntegrationModel
        {
            IntegrationId = Guid.NewGuid(),
            AuthConfig = TestJson.Element("""{"method":"api_key","header":"X-API-Key","token":"secret"}"""),
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "https://nb.example");
        builder.Apply(request, integration);
        Assert.True(request.Headers.Contains("X-API-Key"));
    }

    [Fact]
    public void Apply_tolerates_no_auth()
    {
        var builder = new IntegrationAuthBuilder(NullLogger<IntegrationAuthBuilder>.Instance);
        var integration = new IntegrationModel { IntegrationId = Guid.NewGuid() };  // default (Undefined) config
        var request = new HttpRequestMessage(HttpMethod.Get, "https://nb.example");
        builder.Apply(request, integration);   // must not throw
        Assert.Null(request.Headers.Authorization);
    }
}

public class IntegrationHealthCheckerTests
{
    private readonly FakeUser _caller = new();

    private IntegrationHealthChecker NewChecker(
        AppDbContext db, FakeHttpMessageHandler handler, FakeHttpMessageHandler? tokenHandler = null)
        => new(new IntegrationRepository(db), new IntegrationActionRepository(db),
               _caller, new FakeHttpClientFactory(handler),
               TestAuth.Applier(tokenHandler), NullLogger<IntegrationHealthChecker>.Instance);

    private static Guid SeedIntegration(
        AppDbContext db,
        string? authConfigJson = null,
        string? healthCheckJson = null)
    {
        var id = Guid.NewGuid();
        db.Set<IntegrationModel>().Add(new IntegrationModel
        {
            IntegrationId = id,
            Name = "nb",
            Type = "rest",
            BaseURL = "https://nb.example",
            AuthConfig = authConfigJson is null ? default : TestJson.Element(authConfigJson),
            HealthCheck = healthCheckJson is null ? default : TestJson.Element(healthCheckJson),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static void SeedAction(AppDbContext db, Guid integrationId, string path)
    {
        db.Set<flow_weaver_backend.Models.IntegrationAction>().Add(new()
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = "op-" + Guid.NewGuid().ToString("N")[..6],
            Method = "GET",
            Path = path,
            IsActive = true,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static IntegrationHealthCheckResult Result(ActionResult<IntegrationHealthCheckResult> r)
        => Assert.IsType<IntegrationHealthCheckResult>(Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task Check_missing_integration_returns_404()
    {
        using var db = TestDb.NewContext();
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.OK, "{}"));
        Assert.IsType<NotFoundObjectResult>((await checker.CheckAsync(Guid.NewGuid(), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Check_seeded_integration_probes_endpoint()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, healthCheckJson: """{"path":"/health","expected_status":200}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.OK, "{\"ok\":true}"));

        var res = await checker.CheckAsync(id, CancellationToken.None);
        Assert.Equal("healthy", Result(res).Status);
    }

    // ── Credential verification (the NetBox invalid-token false positive) ──

    // Lenient + credentials + a 403 on the main probe: reachability alone
    // must not win — the service refused the integration's identity.
    [Fact]
    public async Task Lenient_403_with_credentials_is_unhealthy()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """{"method":"token","token":"revoked"}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.Forbidden, """{"detail":"Invalid token"}"""));

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("unhealthy", result.Status);
        Assert.Contains("authentication rejected", result.Error);
    }

    // Lenient + NO credentials + 401: ambiguous — the probed path may be
    // protected while the endpoints the integration actually uses are open
    // (credential-less integrations exist on purpose). Degraded, not
    // unhealthy, with guidance either way.
    [Fact]
    public async Task Lenient_401_without_credentials_is_degraded()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db);
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.Unauthorized));

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("degraded", result.Status);
        Assert.Contains("no credentials configured", result.Error);
    }

    // The exact NETBOX-draft shape: site root answers 200 to ANYONE (the
    // token is never validated there), but the API root derived from the
    // registered actions rejects the invalid token. Must be unhealthy.
    [Fact]
    public async Task Lenient_root200_but_api_root_403_is_unhealthy()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """{"method":"token","token":"revoked"}""");
        SeedAction(db, id, "/api/dcim/devices/");
        SeedAction(db, id, "/api/ipam/ip-addresses/");
        var handler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.StartsWith("/api")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK));
        var checker = NewChecker(db, handler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("unhealthy", result.Status);
        Assert.Contains("/api/", result.Error);
        // Root probe + derived /api/ probe.
        Assert.Equal(2, handler.Requests.Count);
    }

    // Valid token: the API root accepts the authenticated request and
    // rejects the anonymous twin — the credentials were genuinely
    // validated, so healthy with auth_verified=true.
    [Fact]
    public async Task Lenient_api_root_validates_token_is_healthy_verified()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """{"method":"token","token":"good"}""");
        SeedAction(db, id, "/api/dcim/devices/");
        var handler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.StartsWith("/api") && req.Headers.Authorization is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK));
        var checker = NewChecker(db, handler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("healthy", result.Status);
        Assert.True(result.AuthVerified);
    }

    // Everything answers 200 to anyone and there are no registered actions:
    // nothing ever exercised the token, so "healthy" would be a lie — the
    // check reports degraded with auth_verified=false and tells the admin
    // to configure an authenticated health path.
    [Fact]
    public async Task Lenient_everything_anonymous_is_degraded()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """{"method":"token","token":"whatever"}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.OK));

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("degraded", result.Status);
        Assert.False(result.AuthVerified);
        Assert.Contains("health_check.path", result.Error);
    }

    // No credentials at all: reachability is the whole contract — one
    // probe, healthy, no auth verdict.
    [Fact]
    public async Task Lenient_no_credentials_single_probe_healthy()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db);
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound);
        var checker = NewChecker(db, handler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("healthy", result.Status);
        Assert.Null(result.AuthVerified);
        Assert.Single(handler.Requests);
    }

    // oauth2_client_credentials: a successful grant is itself the credential
    // verification (the authorization server validated the client), so the
    // check is healthy + auth_verified without any differential probing.
    [Fact]
    public async Task Lenient_oauth_grant_success_is_healthy_verified()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """
            {"method":"oauth2_client_credentials","token_url":"https://idp.example.com/token","client_id":"cid","client_secret":"cs"}
            """);
        var tokenHandler = new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.OK), tokenHandler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("healthy", result.Status);
        Assert.True(result.AuthVerified);
    }

    // oauth2_client_credentials: the grant succeeded (credentials proven
    // valid by the IdP) but the probed path answers 403 — e.g. Action1's
    // /api/3.0/ root, which rejects EVERYONE. "Rotate the token" would be
    // wrong advice and unhealthy a false negative: degraded + guidance to
    // set health_check.path to a real endpoint.
    [Fact]
    public async Task Lenient_oauth_grant_ok_but_probe_403_is_degraded()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """
            {"method":"oauth2_client_credentials","token_url":"https://idp.example.com/token","client_id":"cid","client_secret":"cs"}
            """);
        var tokenHandler = new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.Forbidden), tokenHandler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("degraded", result.Status);
        Assert.True(result.AuthVerified);
        Assert.Contains("token grant succeeded", result.Error);
    }

    // oauth2_client_credentials: a failed grant means the credentials are
    // broken — unhealthy with the token endpoint's reason, before any API
    // request is even attempted.
    [Fact]
    public async Task Lenient_oauth_grant_failure_is_unhealthy()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, authConfigJson: """
            {"method":"oauth2_client_credentials","token_url":"https://idp.example.com/token","client_id":"cid","client_secret":"bad"}
            """);
        var tokenHandler = new FakeHttpMessageHandler(
            HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var checker = NewChecker(db, new FakeHttpMessageHandler(HttpStatusCode.OK), tokenHandler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("unhealthy", result.Status);
        Assert.Contains("OAuth token grant failed", result.Error);
    }

    // A full URL pasted into health_check.path (the Action1 setup mistake:
    // the token URL went into the path field) must fail with a precise
    // message BEFORE any request — not probe "<base>/https:/…" and surface
    // the origin's baffling 403.
    [Fact]
    public async Task Strict_full_url_in_health_path_is_rejected_without_probing()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db, healthCheckJson: """
            {"path":"https://idp.example.com/oauth/token","expected_status":200}
            """);
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        var checker = NewChecker(db, handler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("unhealthy", result.Status);
        Assert.Contains("RELATIVE", result.Error);
        Assert.Empty(handler.Requests);
    }

    // Strict mode is the admin's explicit contract: exact status match,
    // no extra probes, no auth verdict — even with credentials configured.
    [Fact]
    public async Task Strict_mode_probes_once_and_honors_expected_status()
    {
        using var db = TestDb.NewContext();
        var id = SeedIntegration(db,
            authConfigJson: """{"method":"token","token":"revoked"}""",
            healthCheckJson: """{"path":"/api/","expected_status":200}""");
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Forbidden);
        var checker = NewChecker(db, handler);

        var result = Result(await checker.CheckAsync(id, CancellationToken.None));
        Assert.Equal("unhealthy", result.Status);
        Assert.Single(handler.Requests);
        Assert.Null(result.AuthVerified);
    }
}

public class IntegrationHealthCheckerApiRootTests
{
    // Common prefix of real NetBox-style paths → the API root.
    [Fact]
    public void CommonApiRoot_netbox_paths()
        => Assert.Equal("/api/", IntegrationHealthChecker.CommonApiRoot(
            new[] { "/api/dcim/devices/", "/api/ipam/ip-addresses/" }));

    // Single action: capped at two segments so the probe never lands on a
    // concrete collection endpoint.
    [Fact]
    public void CommonApiRoot_caps_depth_at_two_segments()
        => Assert.Equal("/api/dcim/", IntegrationHealthChecker.CommonApiRoot(
            new[] { "/api/dcim/devices/" }));

    // Templated segments end the prefix.
    [Fact]
    public void CommonApiRoot_stops_at_templates()
        => Assert.Equal("/api/", IntegrationHealthChecker.CommonApiRoot(
            new[] { "/api/{resource}/list" }));

    [Fact]
    public void CommonApiRoot_no_common_prefix_returns_null()
        => Assert.Null(IntegrationHealthChecker.CommonApiRoot(
            new[] { "/api/x", "/rest/y" }));

    [Fact]
    public void CommonApiRoot_empty_returns_null()
        => Assert.Null(IntegrationHealthChecker.CommonApiRoot(Array.Empty<string>()));
}

// IntegrationCatalogService now has a full suite in
// IntegrationCatalogServiceTests.cs (attach spec/skill, non-destructive action
// upsert, bundle reads). The single 404 smoke test that lived here is covered
// there by GetBundle_UnknownIntegration_IsNotFound.
