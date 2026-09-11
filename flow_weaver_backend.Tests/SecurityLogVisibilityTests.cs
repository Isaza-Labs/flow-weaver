using System.Security.Claims;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using flow_weaver_backend.BackgroundServices;

namespace flow_weaver_backend.Tests;

// Sign-in failures for an unknown username never resolve to a user row, so
// they are stored with UserId null. AuthEvent documents them as kept "for
// anomaly detection": they must stay hidden from the default feed (so they
// don't drown out failures on real accounts) yet remain readable when an
// admin explicitly asks for them — otherwise they are write-only data.
public class SecurityLogVisibilityTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public AuthController Build()
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"));
            return new AuthController(null!, Db, new FakeHostEnvironment())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = user },
                },
            };
        }

        // userId null == the unattributed case (unknown username probe).
        public void Seed(string evt, Guid? userId = null, string? attempted = null)
        {
            Db.AuthEvents.Add(new AuthEvent
            {
                AuthEventId = Guid.NewGuid(),
                UserId = userId,
                Event = evt,
                Ip = "1.2.3.4",
                UserAgent = "curl",
                At = DateTime.UtcNow,
                Metadata = TestJson.Element(
                    attempted is null ? "{}" : $"{{\"attempted_username\":\"{attempted}\"}}"),
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class FakeHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static List<AuthEventResponse> Rows(ActionResult<IEnumerable<AuthEventResponse>> result)
        => ((IEnumerable<AuthEventResponse>)((OkObjectResult)result.Result!).Value!).ToList();

    // Unknown-username probes would otherwise drown out failures on real
    // accounts, so the default feed leaves them out.
    [Fact]
    public async Task AuthEvents_UnattributedFailuresAreHiddenByDefault()
    {
        using var f = new Fixture();
        f.Seed("login_failure", attempted: "probe");

        Assert.Empty(Rows(await f.Build().Events()));
    }

    [Fact]
    public async Task AuthEvents_UnattributedFailuresAreVisibleWhenAskedFor()
    {
        using var f = new Fixture();
        f.Seed("login_failure", attempted: "probe");

        var row = Assert.Single(Rows(await f.Build().Events(include_unattributed: true)));

        Assert.Equal("login_failure", row.Event);
        Assert.Null(row.UserId);
    }

    [Fact]
    public async Task AuthEvents_AttributedRowsAreStillReturnedWithoutTheFlag()
    {
        using var f = new Fixture();
        f.Seed("login_success", userId: Guid.NewGuid());

        Assert.Single(Rows(await f.Build().Events()));
    }

    // ─── Retention ──────────────────────────────────────────────────────

    private static SecurityLogRetentionHostedService Sweeper(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddLogging();
        var sp = services.BuildServiceProvider();

        return new SecurityLogRetentionHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<SecurityLogRetentionHostedService>.Instance);
    }

    // Both windows unlimited => the loop must not start at all, rather than
    // waking every six hours to delete nothing. Asserted by the service
    // returning promptly instead of parking on its sweep delay.
    //
    // This is the shipped default: audit_logs is the compliance record, and
    // deleting it would be the wrong call for anyone who has to answer "who
    // changed what" about last year, so the audit sweep is opt-in.
    [Fact]
    public async Task Retention_DoesNotRunWhenBothWindowsAreUnlimited()
    {
        var sweeper = Sweeper(new Dictionary<string, string?>
        {
            ["Auth:RetentionDays"] = "0",
            ["Audit:RetentionDays"] = "0",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sweeper.StartAsync(cts.Token);
        var executing = sweeper.ExecuteTask;

        Assert.NotNull(executing);
        await executing!;                        // returns immediately, no delay loop
        Assert.True(executing.IsCompletedSuccessfully);
        Assert.False(cts.IsCancellationRequested);
    }

    // With a window configured the loop DOES start — it parks on its sweep
    // delay rather than completing, which is what distinguishes it from the
    // disabled case above.
    [Fact]
    public async Task Retention_StartsTheLoopWhenAWindowIsConfigured()
    {
        var sweeper = Sweeper(new Dictionary<string, string?>
        {
            ["Auth:RetentionDays"] = "180",
        });

        await sweeper.StartAsync(CancellationToken.None);
        var executing = sweeper.ExecuteTask;

        Assert.NotNull(executing);
        Assert.False(executing!.IsCompleted);
        await sweeper.StopAsync(CancellationToken.None);
    }
}
