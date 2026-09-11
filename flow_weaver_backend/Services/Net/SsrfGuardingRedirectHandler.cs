using System.Net;

namespace flow_weaver_backend.Services.Net;

/// <summary>
/// Follows HTTP redirects manually, running <see cref="IUrlGuard"/> on every
/// hop.
/// </summary>
/// <remarks>
/// <para>
/// Call sites validate the URL they were handed, but until this handler existed
/// nothing validated where that URL <em>led</em>: every named HttpClient used
/// the framework default (<c>AllowAutoRedirect = true</c>), so an allowed
/// external host could answer <c>302 Location: http://169.254.169.254/…</c> and
/// the socket followed it with the guard none the wiser. That is the textbook
/// SSRF-filter bypass, and one redirect defeated every call site at once —
/// <c>rest_call</c>, <c>integration_action</c>, the agent's
/// <c>execute_operation</c>, MCP connections, the integration health check and
/// the OAuth token grant.
/// </para>
/// <para>
/// The handler deliberately does NOT re-check the initial URI. Each caller
/// already validated it with the policy that belongs to it (an integration's
/// <c>AllowPrivateNetwork</c>, the "same-origin backend" exemption in
/// RestOperationExecutor, …). Re-running the guard here with a generic policy
/// would have silently broken every on-prem integration that legitimately
/// points at 10/8. Hops are the gap, so hops are what this closes.
/// </para>
/// <para>
/// Registration has two halves and BOTH are required: the primary handler must
/// set <c>AllowAutoRedirect = false</c> (otherwise the socket follows redirects
/// below us and the per-hop check is dead code), and this handler must be in
/// the pipeline. <see cref="GuardedHttpClientExtensions.AddGuardedHttpClient"/>
/// wires both — use it rather than doing it by hand.
/// </para>
/// <para>
/// Per-request policy travels in <see cref="HttpRequestMessage.Options"/> via
/// <see cref="AllowPrivateNetworkOption"/>, so one registered handler serves
/// callers with different rules. It defaults to false: a hop into RFC-1918 is
/// refused unless the caller opted in, exactly like the initial check.
/// 169.254.169.254 and loopback stay blocked either way — <c>IUrlGuard</c>
/// never lets <c>allowPrivate</c> bypass those.
/// </para>
/// </remarks>
public sealed class SsrfGuardingRedirectHandler : DelegatingHandler
{
    /// <summary>
    /// Per-request equivalent of <c>IUrlGuard.EnsureSafe(allowPrivate:)</c>,
    /// applied to redirect targets. Set it from the same flag the call site
    /// passes to its own <c>EnsureSafe</c> so a hop is judged like the original.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateNetworkOption =
        new("flowweaver.allow_private_network");

    // A chain longer than this is a loop or an attack, not a service being
    // helpful. Matches what HttpClient's own follower would consider sane for
    // an API client (its default of 50 is a browser-era number).
    private const int MaxRedirects = 5;

    private readonly IUrlGuard _guard;
    private readonly ILogger<SsrfGuardingRedirectHandler> _logger;

    public SsrfGuardingRedirectHandler(
        IUrlGuard guard, ILogger<SsrfGuardingRedirectHandler> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var allowPrivate =
            request.Options.TryGetValue(AllowPrivateNetworkOption, out var ap) && ap;

        var current = request;
        var origin = request.RequestUri;

        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, ct);

            var next = ResolveRedirect(response, current.RequestUri!);
            if (next is null) return response;

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    $"too many redirects (>{MaxRedirects}) starting at {origin}");
            }

            // The whole point: a redirect target is a NEW destination the
            // caller never vetted, so it gets the full guard before we connect.
            _guard.EnsureSafe(next.ToString(), allowPrivate);

            _logger.LogDebug(
                "net.redirect.follow hop={Hop} status={Status} to_host={ToHost}",
                hop + 1, (int)response.StatusCode, next.Host);

            HttpRequestMessage cloned;
            try
            {
                cloned = await CloneForRedirectAsync(current, response.StatusCode, next, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // A body we can't replay (a one-shot stream). Hand the 3xx back
                // rather than silently turning a POST into a GET nobody asked for.
                _logger.LogWarning(
                    "net.redirect.not_followed status={Status} to_host={ToHost} reason={Reason}",
                    (int)response.StatusCode, next.Host, ex.Message);
                return response;
            }

            response.Dispose();
            // Only dispose requests we created — `request` belongs to the caller.
            if (!ReferenceEquals(current, request)) current.Dispose();
            current = cloned;
        }
    }

    // The redirect target, or null when this isn't a redirect we follow.
    private static Uri? ResolveRedirect(HttpResponseMessage response, Uri requestUri)
    {
        var followable = response.StatusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
        if (!followable) return null;

        var location = response.Headers.Location;
        if (location is null) return null;

        // A relative Location is legal (RFC 9110 §10.2.2) — resolve against the
        // URI we just requested, not the original, so a chain of relative hops
        // lands where the server means.
        var absolute = location.IsAbsoluteUri ? location : new Uri(requestUri, location);

        // Only http(s) is followable. `Location: file:///etc/passwd` is a
        // redirect we refuse rather than resolve.
        return absolute.Scheme is "http" or "https" ? absolute : null;
    }

    private static async Task<HttpRequestMessage> CloneForRedirectAsync(
        HttpRequestMessage source, HttpStatusCode status, Uri target, CancellationToken ct)
    {
        // 301/302/303 degrade to GET and drop the body — what browsers and
        // HttpClient itself do. 307/308 preserve method and body, so the body
        // has to be replayable.
        var keepMethodAndBody = status is HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

        var clone = new HttpRequestMessage(
            keepMethodAndBody ? source.Method : HttpMethod.Get, target)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };

        foreach (var (key, value) in source.Options)
            ((IDictionary<string, object?>)clone.Options)[key] = value;

        var sameOrigin = SameOrigin(source.RequestUri!, target);
        foreach (var header in source.Headers)
        {
            // Credentials must not follow a redirect across an origin boundary,
            // or a hostile (or merely compromised) endpoint harvests the
            // integration's bearer just by answering 302. Same-origin hops keep
            // it so an http→https upgrade doesn't break auth.
            if (!sameOrigin && IsCredentialHeader(header.Key)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (keepMethodAndBody && source.Content is not null)
        {
            // Buffer once so the content can be written a second time. Throws
            // for a one-shot stream, which the caller turns into
            // "return the 3xx unfollowed".
            await source.Content.LoadIntoBufferAsync(ct);
            var bytes = await source.Content.ReadAsByteArrayAsync(ct);
            var content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }

    // Anything that authenticates us to the origin. The api-key names are the
    // ones IntegrationAuthBuilder and the OpenAPI `apiKey` scheme emit; a
    // custom header name we can't know is the residual risk of the
    // cross-origin case, which is why redirects are capped at five.
    private static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("X-Auth-Token", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Private-Token", StringComparison.OrdinalIgnoreCase);

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}

/// <summary>
/// Registers a named HttpClient whose redirects are guarded. Use this instead of
/// a bare <c>AddHttpClient</c> for any client that fetches a URL influenced by
/// user, agent or integration input.
/// </summary>
public static class GuardedHttpClientExtensions
{
    public static IHttpClientBuilder AddGuardedHttpClient(
        this IServiceCollection services,
        string name,
        Action<HttpClient> configureClient,
        Func<HttpClientHandler>? primaryHandler = null)
    {
        services.AddTransient<SsrfGuardingRedirectHandler>();

        return services.AddHttpClient(name, configureClient)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = primaryHandler?.Invoke() ?? new HttpClientHandler();
                // Non-negotiable: with auto-redirect on, the socket follows the
                // 302 before SsrfGuardingRedirectHandler ever sees it.
                handler.AllowAutoRedirect = false;
                return handler;
            })
            .AddHttpMessageHandler<SsrfGuardingRedirectHandler>();
    }
}

/// <summary>Per-request knobs for <see cref="SsrfGuardingRedirectHandler"/>.</summary>
public static class GuardedRequestExtensions
{
    /// <summary>
    /// Judge redirect targets with the same private-network policy the caller
    /// used for the initial URL. Pass the integration / MCP server's
    /// <c>AllowPrivateNetwork</c> flag.
    /// </summary>
    public static HttpRequestMessage WithPrivateNetworkPolicy(
        this HttpRequestMessage message, bool allowPrivate)
    {
        message.Options.Set(SsrfGuardingRedirectHandler.AllowPrivateNetworkOption, allowPrivate);
        return message;
    }
}
