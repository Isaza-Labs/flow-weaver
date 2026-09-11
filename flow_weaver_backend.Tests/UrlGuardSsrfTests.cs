using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Traceability: B-08 (TC-FW-B08) and the SSRF portion of NFR-006 (TC-FW-032).
// Exercises IUrlGuard/UrlGuard: requests to loopback, RFC-1918 private ranges,
// link-local / cloud-metadata (169.254/16) and reserved (0/8) addresses are
// blocked — and the per-integration allowPrivate flag opens RFC-1918 WITHOUT
// ever opening the cloud-metadata IP.
//
// Every case uses an IP literal or a well-known blocked hostname, so the guard
// either short-circuits on the hostname layer or Dns.GetHostAddresses returns
// the parsed literal without a query — the suite is deterministic and offline.
public class UrlGuardSsrfTests
{
    private static UrlGuard Guard(bool allowInternal = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowInternalUrls"] = allowInternal ? "true" : "false",
            })
            .Build();
        return new UrlGuard(config, NullLogger<UrlGuard>.Instance);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public void Blocks_wellknown_loopback_and_metadata_hosts(string url)
    {
        // These match the hostname blocklist and throw before any DNS lookup.
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url));
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]      // 10.0.0.0/8
    [InlineData("http://172.16.5.4/")]    // 172.16.0.0/12 low edge
    [InlineData("http://172.31.255.1/")]  // 172.16.0.0/12 high edge
    [InlineData("http://192.168.1.1/")]   // 192.168.0.0/16
    public void Blocks_rfc1918_private_ranges_by_default(string url)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url));
    }

    [Theory]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://192.168.1.1/")]
    public void Allows_rfc1918_when_caller_opts_in(string url)
    {
        // The admin explicitly trusted an internal endpoint via the
        // integration's AllowPrivateNetwork flag. No exception expected.
        Guard().EnsureSafe(url, allowPrivate: true);
    }

    [Fact]
    public void Link_local_metadata_is_never_opened_by_allowPrivate()
    {
        // 169.254/16 stays blocked even with allowPrivate=true — the cloud
        // metadata range is the worst SSRF target. Uses .1.1 (not the .169.254
        // literal) so it exercises the numeric link-local branch rather than
        // the hostname short-circuit.
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureSafe("http://169.254.1.1/", allowPrivate: true));
    }

    [Fact]
    public void Blocks_reserved_zero_range()
    {
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureSafe("http://0.1.2.3/"));
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.15.0.1/")]  // just below 172.16/12 — public
    [InlineData("http://172.32.0.1/")]  // just above 172.16/12 — public
    public void Allows_public_addresses(string url)
    {
        Guard().EnsureSafe(url);
    }

    [Fact]
    public void Invalid_url_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureSafe("not a url"));
    }

    [Fact]
    public void AllowInternalUrls_config_disables_the_guard()
    {
        // Escape hatch for dev/test — every otherwise-blocked target passes.
        var guard = Guard(allowInternal: true);
        guard.EnsureSafe("http://127.0.0.1/");
        guard.EnsureSafe("http://169.254.169.254/");
        guard.EnsureSafe("http://10.0.0.5/");
    }

    // ── IPv6 (incl. IPv4-mapped) ──────────────────────────────────────────

    [Theory]
    [InlineData("http://[::1]/")]                       // IPv6 loopback
    [InlineData("http://[::]/")]                        // unspecified
    [InlineData("http://[fe80::1]/")]                   // link-local
    [InlineData("http://[fc00::1]/")]                   // unique-local (ULA) low
    [InlineData("http://[fd12:3456::1]/")]              // unique-local (ULA) high
    [InlineData("http://[::ffff:127.0.0.1]/")]          // IPv4-mapped loopback
    [InlineData("http://[::ffff:10.0.0.5]/")]           // IPv4-mapped private
    [InlineData("http://[::ffff:169.254.169.254]/")]    // IPv4-mapped metadata
    public void Blocks_ipv6_internal_and_mapped_targets(string url)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url));
    }

    [Fact]
    public void Ipv6_link_local_and_mapped_metadata_are_never_opened_by_allowPrivate()
    {
        // fe80::/10 and the IPv4-mapped cloud-metadata IP stay blocked even with
        // allowPrivate=true — same rule as IPv4 169.254/16.
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureSafe("http://[fe80::1]/", allowPrivate: true));
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureSafe("http://[::ffff:169.254.169.254]/", allowPrivate: true));
    }

    [Fact]
    public void Ipv6_unique_local_opens_with_allowPrivate()
    {
        // ULA (fc00::/7) is the IPv6 analog of RFC-1918 — openable by opt-in.
        Guard().EnsureSafe("http://[fc00::1]/", allowPrivate: true);
    }

    [Fact]
    public void Allows_public_ipv6()
    {
        Guard().EnsureSafe("http://[2606:4700:4700::1111]/"); // global unicast
    }

    // ── scheme allowlist (EnsureSafe only) ────────────────────────────────

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://8.8.8.8/")]
    [InlineData("ftp://8.8.8.8/")]
    [InlineData("smtp://8.8.8.8:25")]
    public void Rejects_non_http_schemes(string url)
    {
        // The address rules below the scheme check assume an HTTP dial. A
        // scheme that doesn't have one makes them meaningless, so it never gets
        // that far.
        var ex = Assert.Throws<InvalidOperationException>(() => Guard().EnsureSafe(url));
        Assert.Contains("scheme", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── EnsureHostSafe: same address rules, no scheme ─────────────────────
    //
    // Regression pin. EmailSender used to build a synthetic `smtp://host:port`
    // to reuse EnsureSafe, which worked only while the guard ignored the
    // scheme. Adding the allowlist broke every email_send with
    // "SSRF blocked: scheme 'smtp' is not allowed". A caller holding a host for
    // a non-HTTP protocol has its own entry point now, and these cases keep it
    // that way.

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("[2606:4700:4700::1111]")]
    public void EnsureHostSafe_allows_a_public_host_with_no_scheme(string host)
    {
        Guard().EnsureHostSafe(host);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("[::1]")]
    public void EnsureHostSafe_blocks_internal_hosts(string host)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureHostSafe(host));
    }

    [Fact]
    public void EnsureHostSafe_honours_allowPrivate_without_opening_metadata()
    {
        // An internal SMTP relay on 10/8 is the normal case for a self-hosted
        // deployment; the metadata endpoint never is.
        Guard().EnsureHostSafe("10.0.0.5", allowPrivate: true);
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureHostSafe("169.254.169.254", allowPrivate: true));
        Assert.Throws<InvalidOperationException>(
            () => Guard().EnsureHostSafe("127.0.0.1", allowPrivate: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureHostSafe_rejects_an_empty_host(string host)
    {
        Assert.Throws<InvalidOperationException>(() => Guard().EnsureHostSafe(host));
    }

    [Fact]
    public void EnsureHostSafe_is_disabled_by_AllowInternalUrls_like_EnsureSafe()
    {
        // The dev kill switch has to cover both entry points, or turning it on
        // fixes HTTP and leaves SMTP failing for no visible reason.
        Guard(allowInternal: true).EnsureHostSafe("127.0.0.1");
    }
}
