using System.Security.Claims;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Auth;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The auth surface's two edges: the dev-only bootstrap escape hatch (which
// must 404 outside Development so there is no leak surface in production) and
// the auth-event feed an admin uses to investigate a suspicious login.
//
// The dev seeder is included because it deliberately bypasses the password
// policy — a change that made it run outside Development would put a known
// admin/admin account on a real deployment.
public class AuthSurfaceBatchTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // ─── AuthController ─────────────────────────────────────────────────

    private sealed class RecordingAuthService : IAuthService
    {
        public int BootstrapCalls { get; private set; }
        public string? LastIp { get; private set; }
        public string? LastUserAgent { get; private set; }

        public Task<ActionResult<LoginResponse>> LoginAsync(
            LoginRequest request, string ip, string userAgent, CancellationToken ct)
        {
            LastIp = ip;
            LastUserAgent = userAgent;
            return Task.FromResult<ActionResult<LoginResponse>>(new LoginResponse());
        }

        public Task<ActionResult<LoginResponse>> RefreshAsync(
            RefreshRequest request, string ip, string userAgent, CancellationToken ct)
            => Task.FromResult<ActionResult<LoginResponse>>(new LoginResponse());

        public Task<ActionResult> LogoutAsync(
            RefreshRequest request, Guid userId, string ip, string userAgent, CancellationToken ct)
            => Task.FromResult<ActionResult>(new NoContentResult());

        public Task<ActionResult> ChangePasswordAsync(
            ChangePasswordRequest request, Guid userId,
            string ip, string userAgent, CancellationToken ct)
            => Task.FromResult<ActionResult>(new NoContentResult());

        public Task<ActionResult<MeResponse>> MeAsync(Guid userId, CancellationToken ct)
            => Task.FromResult<ActionResult<MeResponse>>(new MeResponse());

        public Task<ActionResult<BootstrapResponse>> BootstrapAsync(
            BootstrapRequest? request, string ip, string userAgent, CancellationToken ct)
        {
            BootstrapCalls++;
            return Task.FromResult<ActionResult<BootstrapResponse>>(new BootstrapResponse());
        }
    }

    private sealed class FixedEnvironment : IWebHostEnvironment
    {
        public FixedEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AuthFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingAuthService Auth { get; } = new();

        public AuthController Build(string environment = "Development")
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, User.ToString()),
                }, "test")),
            };
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.9");
            http.Request.Headers.UserAgent = "curl/8";

            return new AuthController(Auth, Db, new FixedEnvironment(environment))
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        public Guid SeedEvent(
            string @event = "login.ok", Guid? userId = null, DateTime? at = null)
        {
            var id = Guid.NewGuid();
            Db.AuthEvents.Add(new AuthEvent
            {
                AuthEventId = id,
                UserId = userId ?? User,
                Event = @event,
                Ip = "10.0.0.9",
                UserAgent = "curl/8",
                Metadata = TestJson.Element("""{"k":1}"""),
                At = at ?? DateTime.UtcNow,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // The bootstrap endpoint creates the first admin with no authentication.
    // Outside Development, as soon as ANY user exists it must not merely
    // refuse — it 404s so there is no surface to probe.
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    public async Task Bootstrap_Is404OutsideDevelopmentOnceAUserExists(string environment)
    {
        using var f = new AuthFixture();
        f.Db.Users.Add(new Models.User
        {
            UserId = Guid.NewGuid(),
            Username = "existing",
            Email = "e@x",
            Role = "admin",
            IsActive = true,
        });
        f.Db.SaveChanges();

        var result = await f.Build(environment).Bootstrap(null, default);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, f.Auth.BootstrapCalls);
    }

    // First-run: a fresh database has nobody who could log in, so the deploy
    // wizard may create the initial admin even in Production.
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Bootstrap_FirstRunIsReachableOutsideDevelopment(string environment)
    {
        using var f = new AuthFixture();

        await f.Build(environment).Bootstrap(null, default);

        Assert.Equal(1, f.Auth.BootstrapCalls);
    }

    [Fact]
    public async Task Bootstrap_IsReachableInDevelopment()
    {
        using var f = new AuthFixture();

        await f.Build("Development").Bootstrap(null, default);

        Assert.Equal(1, f.Auth.BootstrapCalls);
    }

    // The caller's IP and user agent are what make an auth event useful for
    // investigating a suspicious login.
    [Fact]
    public async Task Login_ForwardsTheCallersIpAndUserAgent()
    {
        using var f = new AuthFixture();

        await f.Build().Login(new LoginRequest(), default);

        Assert.Equal("10.0.0.9", f.Auth.LastIp);
        Assert.Equal("curl/8", f.Auth.LastUserAgent);
    }

    private static List<AuthEventResponse> Events(ActionResult<IEnumerable<AuthEventResponse>> result)
        => Assert.IsAssignableFrom<IEnumerable<AuthEventResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).ToList();

    [Fact]
    public async Task Events_ReturnsEventsNewestFirst()
    {
        using var f = new AuthFixture();
        var older = f.SeedEvent("login.ok", at: DateTime.UtcNow.AddHours(-2));
        var newer = f.SeedEvent("login.failed", at: DateTime.UtcNow);

        var rows = Events(await f.Build().Events(null, null, null));

        Assert.Equal(new[] { newer, older }, rows.Select(r => r.AuthEventId));
    }

    [Fact]
    public async Task Events_FiltersByUser()
    {
        using var f = new AuthFixture();
        var other = Guid.NewGuid();
        f.SeedEvent(userId: User);
        f.SeedEvent(userId: other);

        Assert.Equal(other, Assert.Single(Events(await f.Build().Events(other, null, null))).UserId);
    }

    [Fact]
    public async Task Events_FiltersByTimeWindow()
    {
        using var f = new AuthFixture();
        f.SeedEvent("old", at: DateTime.UtcNow.AddDays(-10));
        f.SeedEvent("recent", at: DateTime.UtcNow);

        Assert.Equal("recent",
            Assert.Single(Events(await f.Build().Events(null, DateTime.UtcNow.AddDays(-1), null))).Event);
        Assert.Equal("old",
            Assert.Single(Events(await f.Build().Events(null, null, DateTime.UtcNow.AddDays(-1)))).Event);
    }

    // The page size is capped at 500 so an admin can't pull the whole table
    // in one request.
    [Fact]
    public async Task Events_TheLimitIsCapped()
    {
        using var f = new AuthFixture();
        f.SeedEvent();
        f.SeedEvent();

        Assert.Single(Events(await f.Build().Events(null, null, null, limit: 1)));
        Assert.Equal(2, Events(await f.Build().Events(null, null, null, limit: 9999)).Count);
    }

    [Fact]
    public async Task Events_TheResponseCarriesTheStoredMetadata()
    {
        using var f = new AuthFixture();
        f.SeedEvent();

        var row = Assert.Single(Events(await f.Build().Events(null, null, null)));

        Assert.Equal("10.0.0.9", row.Ip);
        Assert.Equal("curl/8", row.UserAgent);
        Assert.Equal(1, row.Metadata.GetProperty("k").GetInt32());
    }

    // ─── DevSeedService ─────────────────────────────────────────────────

    private static ServiceProvider SeedProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IPasswordHasher<flow_weaver_backend.Models.User>,
            PasswordHasher<flow_weaver_backend.Models.User>>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DevSeed_CreatesTheDefaultAdmin()
    {
        using var sp = SeedProvider(Guid.NewGuid().ToString());

        await DevSeedService.SeedAsync(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = Assert.Single(db.Users.ToList());
        Assert.Equal(DevSeedService.DefaultAdminUsername, admin.Username);
        Assert.Equal("admin", admin.Role);
        Assert.True(admin.IsActive);
    }

    // The seeded password is stored hashed, never in the clear — even though
    // the seeder deliberately bypasses the password POLICY.
    [Fact]
    public async Task DevSeed_ThePasswordIsHashedNotStoredInTheClear()
    {
        using var sp = SeedProvider(Guid.NewGuid().ToString());

        await DevSeedService.SeedAsync(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = Assert.Single(db.Users.ToList());
        Assert.NotEqual(DevSeedService.DefaultAdminPassword, admin.PasswordHash);
        Assert.NotEmpty(admin.PasswordHash);

        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<flow_weaver_backend.Models.User>>();
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(admin, admin.PasswordHash, DevSeedService.DefaultAdminPassword));
    }

    // Runs on every dev startup, so a second pass must not duplicate anything
    // or reset a password the developer changed.
    [Fact]
    public async Task DevSeed_IsIdempotent()
    {
        var dbName = Guid.NewGuid().ToString();
        using var sp = SeedProvider(dbName);
        var factory = sp.GetRequiredService<IServiceScopeFactory>();

        await DevSeedService.SeedAsync(factory, NullLogger.Instance);
        string firstHash;
        using (var scope = sp.CreateScope())
            firstHash = scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Users.Single().PasswordHash;

        await DevSeedService.SeedAsync(factory, NullLogger.Instance);

        using var check = sp.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(db.Users.ToList());
        Assert.Equal(firstHash, db.Users.Single().PasswordHash);
    }
}
