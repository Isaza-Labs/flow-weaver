using System.Net;
using System.Security.Claims;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// Two things the audit trail depends on that had no coverage: where the
// client address comes from, and whether the stored rows can actually be
// queried back.
public class ClientIpAndAuditQueryTests
{
    // ─── ClientIp / forwarded headers ───────────────────────────────────

    private static ForwardedHeadersOptions Options(string? trustedProxies, int? forwardLimit = null)
    {
        var settings = new Dictionary<string, string?>();
        if (trustedProxies is not null) settings["Network:TrustedProxies"] = trustedProxies;
        if (forwardLimit is not null) settings["Network:ForwardLimit"] = forwardLimit.Value.ToString();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddForwardedHeadersFromConfig(config);
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    // Runs the real middleware so the test exercises the trust decision
    // rather than re-implementing it.
    private static async Task<string?> ResolveThroughMiddlewareAsync(
        string? trustedProxies, IPAddress peer, string? forwardedFor, int? forwardLimit = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = peer;
        if (forwardedFor is not null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options(trustedProxies, forwardLimit)));

        await middleware.Invoke(ctx);
        return ClientIp.Resolve(ctx);
    }

    // The whole point of the trust list: an untrusted caller cannot forge its
    // own address. Without this, anyone could dodge the login rate limiter and
    // write fabricated origins into the audit log.
    [Fact]
    public async Task ClientIp_IgnoresForwardedForFromAnUntrustedPeer()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "172.28.0.0/16",
            peer: IPAddress.Parse("203.0.113.7"),   // outside the trusted range
            forwardedFor: "1.2.3.4");

        Assert.Equal("203.0.113.7", ip);
    }

    [Fact]
    public async Task ClientIp_HonoursForwardedForFromATrustedNetwork()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "172.28.0.0/16",
            peer: IPAddress.Parse("172.28.0.5"),    // the frontend container
            forwardedFor: "1.2.3.4");

        Assert.Equal("1.2.3.4", ip);
    }

    [Fact]
    public async Task ClientIp_HonoursForwardedForFromAnIndividuallyTrustedProxy()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: "10.1.2.3",
            peer: IPAddress.Parse("10.1.2.3"),
            forwardedFor: "1.2.3.4");

        Assert.Equal("1.2.3.4", ip);
    }

    // Unset => trust nothing. Wrong (the address is the proxy) but not
    // forgeable, which is the right default before an operator has described
    // their topology.
    //
    // This is the trap the feature has to be built around: to
    // ForwardedHeadersMiddleware, empty KnownNetworks + KnownProxies means
    // "skip the check" — trust EVERYONE — not "trust nobody". Clearing the
    // lists while leaving the feature enabled would have made any caller able
    // to forge its own address, which is strictly worse than the bug being
    // fixed. So an empty trust list disables the middleware outright.
    [Fact]
    public async Task ClientIp_TrustsNothingWhenUnconfigured()
    {
        var ip = await ResolveThroughMiddlewareAsync(
            trustedProxies: null,
            peer: IPAddress.Parse("172.28.0.5"),
            forwardedFor: "1.2.3.4");

        Assert.Equal("172.28.0.5", ip);
    }

    [Fact]
    public void ForwardedHeaders_AreDisabledOutrightWhenNothingIsTrusted()
    {
        Assert.Equal(ForwardedHeaders.None, Options(trustedProxies: null).ForwardedHeaders);
        // Every entry unparseable is the same situation as none at all.
        Assert.Equal(ForwardedHeaders.None, Options("not-an-ip").ForwardedHeaders);
    }

    [Fact]
    public void ForwardedHeaders_AreEnabledOnlyWithATrustList()
    {
        var options = Options("172.28.0.0/16");

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
    }

    [Fact]
    public void ForwardedHeaders_ClearsTheLoopbackOnlyDefaults()
    {
        var options = Options(trustedProxies: null);

        // The framework default trusts loopback, which silently disagrees with
        // "TrustedProxies is unset means trust nothing".
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ForwardedHeaders_ParsesAMixedCidrAndHostList()
    {
        var options = Options("172.28.0.0/16, 10.1.2.3 ,10.1.2.4");

        Assert.Single(options.KnownNetworks);
        Assert.Equal(2, options.KnownProxies.Count);
    }

    // A typo must not stop the process booting — logging the wrong IP beats
    // not starting at all. The entry is dropped, the rest still apply.
    [Fact]
    public void ForwardedHeaders_DropsUnparseableEntriesWithoutThrowing()
    {
        var options = Options("not-an-ip, 172.28.0.0/99, 172.28.0.0/16");

        Assert.Single(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ForwardedHeaders_DefaultsToASingleHopAndHonoursAnOverride()
    {
        Assert.Equal(1, Options("172.28.0.0/16").ForwardLimit);
        Assert.Equal(2, Options("172.28.0.0/16", forwardLimit: 2).ForwardLimit);
    }

    [Fact]
    public void ForwardedHeaders_DescribeReportsWhetherAnythingIsTrusted()
    {
        var none = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        Assert.Contains("none", ForwardedHeadersConfiguration.Describe(none));

        var some = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Network:TrustedProxies"] = "172.28.0.0/16" }).Build();
        Assert.Equal("172.28.0.0/16", ForwardedHeadersConfiguration.Describe(some));
    }

    // Kestrel reports IPv4 peers in mapped form on a dual-stack socket. Two
    // spellings of one client would mean two rate-limit partitions and two
    // shapes in the audit log.
    [Fact]
    public void ClientIp_NormalisesIpv6MappedIpv4()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.5");

        Assert.Equal("10.0.0.5", ClientIp.Resolve(ctx));
    }

    [Fact]
    public void ClientIp_ResolveOrUnknownNeverReturnsNull()
    {
        Assert.Equal("unknown", ClientIp.ResolveOrUnknown(null));
        Assert.Equal("unknown", ClientIp.ResolveOrUnknown(new DefaultHttpContext()));
    }

    // ─── Audit query surface ────────────────────────────────────────────

    private sealed class AuditFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public AuditController Build()
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"));
            return new AuditController(Db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = user },
                },
            };
        }

        public AuditEvent Seed(
            Guid? entityId = null, string entityType = "workflow",
            string action = "update", string? actor = null, Guid? userId = null)
        {
            var row = new AuditEvent
            {
                AuditEventId = Guid.NewGuid(),
                UserId = userId,
                Actor = actor,
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                BeforeJson = TestJson.Element("null"),
                AfterJson = TestJson.Element("{}"),
                At = DateTime.UtcNow,
            };
            Db.AuditLogs.Add(row);
            Db.SaveChanges();
            return row;
        }

        public void Dispose() => Db.Dispose();
    }

    private static List<AuditEventResponse> Rows(ActionResult<IEnumerable<AuditEventResponse>> result)
        => ((IEnumerable<AuditEventResponse>)((OkObjectResult)result.Result!).Value!).ToList();

    // "Everything that happened to this one workflow" — EntityId was stored
    // from the start but had no filter, so the most natural audit question had
    // no answer through the API.
    [Fact]
    public async Task Audit_FiltersByEntityId()
    {
        using var f = new AuditFixture();
        var target = Guid.NewGuid();
        f.Seed(entityId: target);
        f.Seed(entityId: Guid.NewGuid());

        var rows = Rows(await f.Build().Events(entity_id: target));

        Assert.Equal(target, Assert.Single(rows).EntityId);
    }

    // Automation has no UserId, so filtering by actor is the only way to ask
    // "what did the scheduled runs change last night".
    [Fact]
    public async Task Audit_FiltersByActor()
    {
        using var f = new AuditFixture();
        f.Seed(actor: "workflow-runner");
        f.Seed(actor: "ada");

        var rows = Rows(await f.Build().Events(actor: "workflow-runner"));

        Assert.Equal("workflow-runner", Assert.Single(rows).Actor);
    }

    [Fact]
    public async Task Audit_TheResponseExposesTheActor()
    {
        using var f = new AuditFixture();
        f.Seed(actor: "git-webhook");

        Assert.Equal("git-webhook", Assert.Single(Rows(await f.Build().Events())).Actor);
    }
}
