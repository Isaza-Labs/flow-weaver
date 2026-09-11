using System.Net;
using System.Net.Sockets;

namespace flow_weaver_backend.Services.Net;

/// <summary>
/// SSRF guard — blocks requests to localhost, private IP ranges, link-local,
/// and the cloud metadata endpoint, for both IPv4 and IPv6 (including
/// IPv4-mapped IPv6 like <c>::ffff:169.254.169.254</c>). Can be overridden via
/// config for dev/testing.
/// </summary>
public sealed class UrlGuard : IUrlGuard
{
    private readonly bool _allowInternal;
    private readonly ILogger<UrlGuard> _logger;

    public UrlGuard(IConfiguration configuration, ILogger<UrlGuard> logger)
    {
        _allowInternal = configuration.GetValue<bool>("Security:AllowInternalUrls");
        _logger = logger;
    }

    public void EnsureSafe(string url, bool allowPrivate = false)
    {
        if (_allowInternal)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                "<invalid>", "invalid_uri");
            throw new InvalidOperationException($"Invalid URL: {url}");
        }

        // Scheme allowlist. Without it `file:///etc/passwd`, `gopher://…` and
        // friends sailed through — the address checks below are meaningless for
        // a scheme that doesn't dial a host the way HTTP does. Everything that
        // reaches THIS method is fetched over HTTP.
        //
        // A caller holding a host for some other protocol (SMTP) must use
        // EnsureHostSafe rather than inventing a URL to get past this: a
        // synthetic `smtp://host:port` used to work by accident, and tightening
        // the scheme check broke email_send until the two cases were separated.
        if (uri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                uri.Host, "scheme_not_allowed");
            throw new InvalidOperationException(
                $"SSRF blocked: scheme '{uri.Scheme}' is not allowed (http/https only)");
        }

        EnsureHostSafe(uri.Host, allowPrivate);
    }

    public void EnsureHostSafe(string host, bool allowPrivate = false)
    {
        if (_allowInternal)
            return;

        host = (host ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}", "<empty>", "empty_host");
            throw new InvalidOperationException("SSRF blocked: no host to check");
        }

        // IPv6 literals arrive bracketed (e.g. "[::1]"); normalize for both the
        // hostname blocklist and the DNS lookup.
        var bareHost = host.Length > 1 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;

        // Block well-known loopback / metadata / unspecified hostnames. `::` and
        // `0.0.0.0` must be caught here — Dns.GetHostAddresses throws for them.
        if (bareHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || bareHost == "127.0.0.1"
            || bareHost == "0.0.0.0"
            || bareHost == "::1"
            || bareHost == "::"
            || bareHost == "169.254.169.254")
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "blocked_hostname");
            throw new InvalidOperationException(
                $"SSRF blocked: requests to {host} are not allowed");
        }

        // Resolve to IP and check numeric ranges.
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(bareHost);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "dns_resolution_failed");
            throw new InvalidOperationException(
                $"SSRF blocked: cannot resolve host {host}", ex);
        }

        foreach (var ip in addresses)
        {
            // Evaluate an IPv4-mapped IPv6 address (::ffff:a.b.c.d) as its
            // embedded IPv4, so an attacker can't smuggle 169.254.169.254 or a
            // loopback past the IPv4 rules by wrapping it in IPv6.
            var checkIp = ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6
                ? ip.MapToIPv4()
                : ip;

            // Loopback (127/8, ::1, ::ffff:127.x) — never bypassed.
            if (IPAddress.IsLoopback(checkIp))
            {
                _logger.LogWarning(
                    "net.url_guard.blocked host={Host} reason={Reason}",
                    host, "loopback_address");
                throw new InvalidOperationException(
                    $"SSRF blocked: {host} resolves to loopback address {ip}");
            }

            if (checkIp.AddressFamily == AddressFamily.InterNetwork)
                CheckIPv4(checkIp.GetAddressBytes(), host, checkIp, allowPrivate);
            else if (checkIp.AddressFamily == AddressFamily.InterNetworkV6)
                CheckIPv6(checkIp, host, allowPrivate);
        }

        _logger.LogDebug("net.url_guard.ok host={Host}", host);
    }

    private void CheckIPv4(byte[] bytes, string host, IPAddress ip, bool allowPrivate)
    {
        // 10.0.0.0/8 — RFC-1918, blocked unless caller opted in.
        if (bytes[0] == 10)
        {
            PrivateOrThrow(allowPrivate, host, ip);
        }
        // 172.16.0.0/12 — RFC-1918.
        else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            PrivateOrThrow(allowPrivate, host, ip);
        }
        // 192.168.0.0/16 — RFC-1918.
        else if (bytes[0] == 192 && bytes[1] == 168)
        {
            PrivateOrThrow(allowPrivate, host, ip);
        }

        // 169.254.0.0/16 (link-local / cloud metadata) — NEVER bypassed by
        // allowPrivate. The cloud-metadata IP is the worst SSRF target.
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "link_local_address");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to link-local address {ip}");
        }
        // 0.0.0.0/8
        if (bytes[0] == 0)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "reserved_address");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to reserved address {ip}");
        }

        // 100.64.0.0/10 — RFC-6598 carrier-grade NAT. Treated as private
        // rather than reserved: it is where a cloud provider's shared
        // infrastructure and some on-prem overlays live, so an admin who set
        // AllowPrivateNetwork on an integration may genuinely need it, but it
        // must not be reachable by default.
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            PrivateOrThrow(allowPrivate, host, ip);
        }

        // Never bypassable, none of these is a legitimate integration target:
        //   192.0.0.0/24    RFC-6890 IETF protocol assignments
        //   192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24  documentation
        //   198.18.0.0/15   RFC-2544 benchmarking
        //   224.0.0.0/4     multicast
        //   240.0.0.0/4     reserved (includes 255.255.255.255 broadcast)
        var reserved =
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            || bytes[0] >= 224;
        if (reserved)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "reserved_address");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to reserved address {ip}");
        }
    }

    private void CheckIPv6(IPAddress ip, string host, bool allowPrivate)
    {
        // Unspecified (::) — reserved.
        if (ip.Equals(IPAddress.IPv6Any))
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "reserved_address");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to unspecified address {ip}");
        }
        // Link-local fe80::/10 — NEVER bypassed (parallels IPv4 169.254/16).
        if (ip.IsIPv6LinkLocal)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "ipv6_link_local");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to IPv6 link-local address {ip}");
        }
        // Site-local fec0::/10 (deprecated).
        if (ip.IsIPv6SiteLocal)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "ipv6_site_local");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to IPv6 site-local address {ip}");
        }
        // Unique-local fc00::/7 (fc00–fdff) — private; blocked unless opted in.
        if ((ip.GetAddressBytes()[0] & 0xFE) == 0xFC)
        {
            PrivateOrThrow(allowPrivate, host, ip);
        }
        // Multicast ff00::/8 — the IPv6 parallel to 224/4, never a valid target.
        if (ip.IsIPv6Multicast)
        {
            _logger.LogWarning(
                "net.url_guard.blocked host={Host} reason={Reason}",
                host, "ipv6_multicast");
            throw new InvalidOperationException(
                $"SSRF blocked: {host} resolves to IPv6 multicast address {ip}");
        }
    }

    private void PrivateOrThrow(bool allowPrivate, string host, IPAddress ip)
    {
        if (allowPrivate)
        {
            _logger.LogDebug("net.url_guard.allow_private host={Host} ip={Ip}", host, ip);
            return;
        }
        _logger.LogWarning(
            "net.url_guard.blocked host={Host} reason={Reason}",
            host, "private_address");
        throw new InvalidOperationException(
            $"SSRF blocked: {host} resolves to private address {ip}");
    }
}
