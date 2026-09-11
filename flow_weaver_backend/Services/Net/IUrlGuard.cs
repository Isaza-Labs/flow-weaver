namespace flow_weaver_backend.Services.Net;

/// <summary>
/// Validates that a URL is safe to request (no SSRF to internal networks).
/// </summary>
public interface IUrlGuard
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the URL targets a
    /// blocked address (localhost, private ranges, cloud metadata, etc.).
    /// </summary>
    /// <param name="url">The absolute URL to validate.</param>
    /// <param name="allowPrivate">When true, RFC-1918 private ranges
    /// (10/8, 172.16/12, 192.168/16) are accepted. Loopback (127/8, ::1),
    /// link-local (169.254/16), the cloud metadata IP, and reserved/0/8
    /// addresses stay blocked even when this flag is true. Used by
    /// integration handlers when the admin explicitly trusted an
    /// internal endpoint via the integration's AllowPrivateNetwork flag.</param>
    void EnsureSafe(string url, bool allowPrivate = false);

    /// <summary>
    /// Same address rules as <see cref="EnsureSafe"/>, for a caller that has a
    /// HOST rather than a URL — an SMTP relay, or anything else that isn't
    /// dialled over HTTP.
    /// </summary>
    /// <remarks>
    /// Exists because <see cref="EnsureSafe"/> enforces an http/https scheme
    /// allowlist, and a non-HTTP caller has no honest URL to hand it. Faking one
    /// (<c>smtp://host:port</c>) worked by accident until that allowlist landed
    /// and broke email_send. Pass the host; do not invent a scheme.
    /// </remarks>
    /// <param name="host">Hostname or IP literal, no scheme and no port.
    /// Bracketed IPv6 (<c>[::1]</c>) is accepted.</param>
    /// <param name="allowPrivate">As <see cref="EnsureSafe"/>.</param>
    void EnsureHostSafe(string host, bool allowPrivate = false);
}
