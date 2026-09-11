using System.Text;
using flow_weaver_backend.BackgroundServices;

namespace flow_weaver_backend.Tests;

// Teams has no Socket Mode, so a no-ingress deployment puts Azure Relay in
// front of the bot: the Relay accepts the Bot Framework's POST and forwards it
// over an outbound control channel this process holds open.
//
// That extra hop is only safe because the activity keeps arriving with its own
// Bearer JWT — the Relay is transport, not authentication, and the request goes
// through the SAME ReceiveAsync (and therefore the same issuer / audience /
// signature / serviceurl checks) as the public webhook. So the property worth
// pinning here is that the mapping loses nothing on the way: body verbatim,
// Authorization intact and findable, and no accidental downgrade to the
// unverified GET-probe path.
public class TeamsRelayRequestMappingTests
{
    private static Task<flow_weaver_backend.Services.Messaging.MessagingHttpRequest> Map(
        string? method = "POST",
        IReadOnlyDictionary<string, string>? headers = null,
        Uri? url = null,
        string? body = null)
        => TeamsRelayHostedService.ToMessagingRequestAsync(
            method, headers, url,
            body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
            default);

    // ─── the JWT survives the hop ───────────────────────────────────────

    [Fact]
    public async Task TheAuthorizationHeaderIsCarriedThrough()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer the.jwt.token" };

        var request = await Map(headers: headers);

        Assert.Equal("Bearer the.jwt.token", request.Header("Authorization"));
    }

    // The provider asks for "Authorization"; the Relay is free to hand us the
    // name in any casing, and a case-sensitive map would silently drop it —
    // which reads as "missing bearer token" rather than as a bug here.
    [Theory]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("AuThOrIzAtIoN")]
    public async Task HeaderLookupIsCaseInsensitiveWhateverTheRelayReports(string name)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "Bearer t" };

        var request = await Map(headers: headers);

        Assert.Equal("Bearer t", request.Header("Authorization"));
    }

    // ─── the body is what the signature/parse sees ──────────────────────

    [Fact]
    public async Task TheBodyIsCarriedThroughByteForByte()
    {
        const string activity = """{"type":"message","text":"hola"}""";

        var request = await Map(body: activity);

        Assert.Equal(activity, Encoding.UTF8.GetString(request.Body));
    }

    // Teams display names and message text are routinely non-ASCII; a mangled
    // body would break both parsing and any byte-exact check over it.
    [Fact]
    public async Task NonAsciiBodyContentIsPreserved()
    {
        const string activity = """{"text":"¿qué dispositivos hay en Málaga? 日本"}""";

        var request = await Map(body: activity);

        Assert.Equal(activity, Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task AnAbsentBodyBecomesEmptyRatherThanNull()
    {
        var request = await Map(body: null);

        Assert.NotNull(request.Body);
        Assert.Empty(request.Body);
    }

    // ─── method handling ────────────────────────────────────────────────

    [Fact]
    public async Task TheMethodIsCarriedThrough()
    {
        var request = await Map(method: "POST");

        Assert.Equal("POST", request.Method);
    }

    // Verification treats a non-POST as a probe and waves it through. A blank
    // method must therefore NOT fall through to that branch — an activity with
    // no reported verb has to face the JWT check like any other.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AMissingMethodDefaultsToPostSoVerificationStillRuns(string? method)
    {
        var request = await Map(method: method);

        Assert.Equal("POST", request.Method);
    }

    // ─── query string ───────────────────────────────────────────────────

    [Fact]
    public async Task QueryParametersAreParsed()
    {
        var request = await Map(url: new Uri("https://ns.servicebus.windows.net/hc?a=1&b=two"));

        Assert.Equal("1", request.QueryValue("a"));
        Assert.Equal("two", request.QueryValue("b"));
    }

    [Fact]
    public async Task PercentEncodedQueryValuesAreDecoded()
    {
        var request = await Map(url: new Uri("https://ns.servicebus.windows.net/hc?q=a%20b%26c"));

        Assert.Equal("a b&c", request.QueryValue("q"));
    }

    // A value containing "=" (base64, tokens) must not be truncated at the
    // first separator.
    [Fact]
    public async Task OnlyTheFirstEqualsSeparatesNameFromValue()
    {
        var request = await Map(url: new Uri("https://ns.servicebus.windows.net/hc?t=abc=def=="));

        Assert.Equal("abc=def==", request.QueryValue("t"));
    }

    [Fact]
    public async Task AValuelessQueryParameterBecomesEmpty()
    {
        var request = await Map(url: new Uri("https://ns.servicebus.windows.net/hc?flag"));

        Assert.Equal(string.Empty, request.QueryValue("flag"));
    }

    [Fact]
    public async Task NoUrlOrQueryIsHandled()
    {
        var withoutUrl = await Map(url: null);
        var withoutQuery = await Map(url: new Uri("https://ns.servicebus.windows.net/hc"));

        Assert.Null(withoutUrl.QueryValue("anything"));
        Assert.Null(withoutQuery.QueryValue("anything"));
    }

    // ─── degenerate input ───────────────────────────────────────────────

    [Fact]
    public async Task NoHeadersAtAllIsHandled()
    {
        var request = await Map(headers: null);

        Assert.Null(request.Header("Authorization"));
    }

    [Fact]
    public async Task ABlankHeaderNameIsSkippedRatherThanStored()
    {
        var headers = new Dictionary<string, string> { [""] = "value", ["X-Real"] = "kept" };

        var request = await Map(headers: headers);

        Assert.Equal("kept", request.Header("X-Real"));
        Assert.Single(request.Headers);
    }
}
