using System.Net;
using Microsoft.AspNetCore.Http;

namespace flow_weaver_backend.Services.Observability;

// Single place that answers "who is calling", for the audit trail, the auth
// event log and the anti-brute-force partition key.
//
// The value is only as good as the ForwardedHeaders configuration: in the
// stock deployment every browser call reaches the backend through the
// SvelteKit node server (frontend/src/hooks.server.ts proxies /api), so
// Connection.RemoteIpAddress is that container, identical for every user on
// the platform. Two things went wrong with that:
//
//   - The login limiter partitions on it, so its 5-attempts-per-minute budget
//     was shared by the entire user base instead of being per-client.
//   - AuditEvent.Ip / AuthEvent.Ip recorded a constant, which is worthless
//     for the "where did this come from" question they exist to answer.
//
// ForwardedHeadersConfiguration rewrites RemoteIpAddress from X-Forwarded-For
// but ONLY for connections coming from a proxy the operator declared trusted,
// so an untrusted caller cannot spoof its own address. This helper just reads
// the result and normalises it.
public static class ClientIp
{
    public static string? Resolve(HttpContext? ctx)
    {
        var address = ctx?.Connection.RemoteIpAddress;
        return address is null ? null : Normalize(address);
    }

    // Same value, never null — for partition keys and non-nullable columns.
    public static string ResolveOrUnknown(HttpContext? ctx)
        => Resolve(ctx) ?? "unknown";

    // Kestrel reports IPv4 clients as IPv6-mapped ("::ffff:10.0.0.5") when the
    // socket is dual-stack. Collapsing to the plain form keeps one client from
    // occupying two rate-limit partitions and two spellings in the audit log.
    private static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        return address.ToString();
    }
}
