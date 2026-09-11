using System.Net;
using flow_weaver_backend.Services.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The redirect half of the SSRF story. UrlGuardSsrfTests covers the address
// rules themselves; this file covers the hole those rules used to sit behind:
// every named HttpClient followed redirects automatically, so an allowed
// external host could answer `302 Location: http://169.254.169.254/…` and the
// socket followed it without the guard ever running on the new destination.
//
// One 302 defeated every call site at once — rest_call, integration_action, the
// agent's execute_operation, MCP and the OAuth token grant all shared the same
// blind spot. Hence the emphasis here on the hop, not the initial URL: the
// handler deliberately leaves the initial URL to the caller, which validates it
// with the policy that belongs to it.
public class SsrfRedirectGuardTests
{
    private static UrlGuard Guard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowInternalUrls"] = "false",
            })
            .Build();
        return new UrlGuard(config, NullLogger<UrlGuard>.Instance);
    }

    // Hosts are IP literals throughout: UrlGuard resolves the target with
    // Dns.GetHostAddresses, so a name would make the suite depend on DNS. A
    // literal is returned as-is, keeping every case offline and deterministic —
    // same convention as UrlGuardSsrfTests. 8.8.8.8 / 1.1.1.1 / 9.9.9.9 stand in
    // for "some public host"; nothing is ever actually dialled because
    // ScriptedHandler terminates the pipeline.
    //
    // Answers a scripted sequence of responses, recording every URI it was
    // asked for so a test can assert what did — and did not — get requested.
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<Uri> Requested { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public ScriptedHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static HttpClient Client(ScriptedHandler inner) =>
        new(new SsrfGuardingRedirectHandler(Guard(), NullLogger<SsrfGuardingRedirectHandler>.Instance)
        {
            InnerHandler = inner,
        });

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud metadata
    [InlineData("http://127.0.0.1:8080/admin")]               // loopback
    [InlineData("http://10.0.0.5/internal")]                  // RFC-1918
    [InlineData("http://[::1]/admin")]                        // IPv6 loopback
    public async Task A_redirect_to_an_internal_address_is_refused(string target)
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.Found, target));
        using var client = Client(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://8.8.8.8/thing"));

        // The decisive assertion: the internal address was never requested.
        Assert.Single(inner.Requested);
        Assert.Equal("8.8.8.8", inner.Requested[0].Host);
    }

    [Fact]
    public async Task A_redirect_to_a_public_address_is_followed()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "https://1.1.1.1/thing"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        var response = await client.GetAsync("https://8.8.8.8/thing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requested.Count);
        Assert.Equal("1.1.1.1", inner.Requested[1].Host);
    }

    [Fact]
    public async Task A_private_hop_is_allowed_only_when_the_caller_opted_in()
    {
        // An on-prem integration with AllowPrivateNetwork=true may legitimately
        // be redirected within its own network — that policy has to reach the
        // hop check, or turning the guard on would break those deployments.
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "http://10.0.0.5/internal"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/api/");
        msg.WithPrivateNetworkPolicy(true);

        var response = await client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("10.0.0.5", inner.Requested[1].Host);
    }

    [Fact]
    public async Task Allow_private_never_opens_the_metadata_endpoint()
    {
        // allowPrivate opens RFC-1918. It must NOT open 169.254/16 — that is
        // the single most valuable SSRF target and IUrlGuard never bypasses it.
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "http://169.254.169.254/latest/meta-data/"));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/api/");
        msg.WithPrivateNetworkPolicy(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(msg));
        Assert.Single(inner.Requested);
    }

    [Fact]
    public async Task Authorization_is_stripped_on_a_cross_origin_hop()
    {
        // Otherwise answering 302 is enough to harvest the integration's bearer.
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "https://9.9.9.9/collect"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/thing");
        msg.Headers.Add("Authorization", "Bearer super-secret");
        msg.Headers.Add("X-API-Key", "also-secret");

        await client.SendAsync(msg);

        Assert.Equal(2, inner.Requests.Count);
        Assert.False(inner.Requests[1].Headers.Contains("Authorization"));
        Assert.False(inner.Requests[1].Headers.Contains("X-API-Key"));
    }

    [Fact]
    public async Task Authorization_survives_a_same_origin_hop()
    {
        // A plain path change on the same origin must not break auth.
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "https://8.8.8.8/v2/thing"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/thing");
        msg.Headers.Add("Authorization", "Bearer super-secret");

        await client.SendAsync(msg);

        Assert.True(inner.Requests[1].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task A_relative_location_resolves_against_the_current_uri()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "/v2/thing"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        await client.GetAsync("https://8.8.8.8/v1/thing");

        Assert.Equal(new Uri("https://8.8.8.8/v2/thing"), inner.Requested[1]);
    }

    [Fact]
    public async Task A_redirect_loop_is_capped()
    {
        var hops = Enumerable.Range(0, 10)
            .Select(i => Redirect(HttpStatusCode.Found, $"https://8.8.8.8/hop{i}"))
            .ToArray();
        var inner = new ScriptedHandler(hops);
        using var client = Client(inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://8.8.8.8/start"));

        Assert.Contains("too many redirects", ex.Message);
        // Initial request + MaxRedirects follows, then it gives up.
        Assert.Equal(6, inner.Requested.Count);
    }

    [Fact]
    public async Task A_non_http_location_is_not_followed()
    {
        // `Location: file:///etc/passwd` is refused rather than resolved; the
        // 3xx is handed back to the caller untouched.
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.Found, "file:///etc/passwd"));
        using var client = Client(inner);

        var response = await client.GetAsync("https://8.8.8.8/thing");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(inner.Requested);
    }

    [Fact]
    public async Task A_307_replays_the_body_to_the_new_target()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.TemporaryRedirect, "https://8.8.8.8/v2/thing"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://8.8.8.8/thing")
        {
            Content = new StringContent("""{"a":1}""", System.Text.Encoding.UTF8, "application/json"),
        };

        await client.SendAsync(msg);

        Assert.Equal(HttpMethod.Post, inner.Requests[1].Method);
        Assert.Equal("""{"a":1}""", await inner.Requests[1].Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_303_downgrades_to_get_and_drops_the_body()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.SeeOther, "https://8.8.8.8/result"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(inner);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://8.8.8.8/thing")
        {
            Content = new StringContent("secret=1"),
        };

        await client.SendAsync(msg);

        Assert.Equal(HttpMethod.Get, inner.Requests[1].Method);
        Assert.Null(inner.Requests[1].Content);
    }
}
