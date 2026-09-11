using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The provider type list is validated in AIProviderService and built in
// LlmProviderFactory. Nothing validated it at all before, so a typo saved happily
// and failed later inside a turn; these tests are what keeps the two ends in step.
public class LlmProviderCatalogTests
{
    [Fact]
    public void Every_type_but_custom_has_a_default_endpoint()
    {
        foreach (var type in LlmProviderCatalog.Types)
        {
            var url = LlmProviderCatalog.DefaultBaseUrl(type);
            if (type == LlmProviderCatalog.Custom)
            {
                Assert.Null(url);
                Assert.True(LlmProviderCatalog.RequiresBaseUrl(type));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(url), $"{type} has no default base URL");
                Assert.False(LlmProviderCatalog.RequiresBaseUrl(type));
            }
        }
    }

    [Theory]
    [InlineData("openai", true)]
    [InlineData("gemini", true)]
    [InlineData("deepseek", true)]
    [InlineData("kimi", true)]
    [InlineData("custom", true)]
    [InlineData("OpenAI", true)] // the field is normalised, so casing must not decide
    [InlineData("claude", false)]
    [InlineData("bedrock", false)]
    [InlineData(null, false)]
    public void IsSupported_matches_the_catalog(string? type, bool expected)
        => Assert.Equal(expected, LlmProviderCatalog.IsSupported(type));
}

// DeepSeek, Kimi, Gemini's compatibility endpoint and the `custom` type all run
// through OpenAiProvider, which puts two things under test that did not matter
// while it only served OpenAI: the URL it builds from an operator-supplied base,
// and what it does when an endpoint does not understand a request field.
public class OpenAiCompatibleEndpointTests
{
    private static FakeHttpMessageHandler Sequenced(string okBody, int badRequests = 0)
    {
        var seen = 0;
        return new FakeHttpMessageHandler(_ =>
            ++seen <= badRequests
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"unknown field: stream_options"}}""",
                        Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(okBody, Encoding.UTF8, "text/event-stream"),
                });
    }

    private const string DoneSse = "data: [DONE]\n\n";

    private static OpenAiProvider Build(
        FakeHttpMessageHandler handler, string? baseUrl = null,
        string providerType = "custom", string chatPath = OpenAiProvider.DefaultChatPath)
        => new(new HttpClient(handler, disposeHandler: false), "k", baseUrl,
            NullLogger<OpenAiProvider>.Instance, null, providerType, chatPath);

    private static async Task Drain(OpenAiProvider provider)
    {
        await foreach (var _ in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], new(), "m", 0.0, default)) { }
    }

    // The vendors publish base URLs with the version already on them — DeepSeek's is
    // https://api.deepseek.com/v1, Gemini's compatibility one ends in /v1beta/openai
    // — so pasting the documented value is the normal case, and it must not double
    // the segment into a 404 that reads like a bad key.
    [Theory]
    [InlineData(null, "/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.moonshot.ai/v1/", "/v1/chat/completions", "https://api.moonshot.ai/v1/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com", "/v1beta/openai/chat/completions",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "/v1beta/openai/chat/completions",
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("https://gw.internal/openai/v1", "/v1/chat/completions", "https://gw.internal/openai/v1/chat/completions")]
    public async Task RequestUrlDoesNotRepeatASegmentTheBaseUrlAlreadyHas(
        string? baseUrl, string chatPath, string expected)
    {
        var handler = Sequenced(DoneSse);

        await Drain(Build(handler, baseUrl, chatPath: chatPath));

        Assert.Equal(expected, Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    // An endpoint that rejects `stream_options` used to take the whole provider down
    // with a non-retryable 400. The field only buys token accounting, so it is
    // dropped and the turn runs.
    [Fact]
    public async Task A400OnStreamOptionsIsRetriedOnceWithoutIt()
    {
        var handler = Sequenced(DoneSse, badRequests: 1);

        await Drain(Build(handler, "https://gw.internal"));

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("stream_options", handler.RequestBodies[0]);
        Assert.DoesNotContain("stream_options", handler.RequestBodies[1]);
    }

    // And it is not asked for again for the rest of the turn: one instance serves
    // every tool-calling round, so a second round must not pay the extra request.
    [Fact]
    public async Task OnceRejectedStreamOptionsIsNotSentAgainByThatInstance()
    {
        var handler = Sequenced(DoneSse, badRequests: 1);
        var provider = Build(handler, "https://gw.internal");

        await Drain(provider);
        await Drain(provider);

        Assert.Equal(3, handler.RequestBodies.Count); // 400 + fallback + second round
        Assert.DoesNotContain("stream_options", handler.RequestBodies[2]);
    }

    // A 400 with an unrelated cause costs one extra request and then surfaces, rather
    // than being swallowed into a turn that silently does nothing.
    [Fact]
    public async Task A400ThatIsNotAboutStreamOptionsStillFails()
    {
        var handler = Sequenced(DoneSse, badRequests: 99);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await Drain(Build(handler, "https://gw.internal")));
    }

    // Usage still rides on stream_options where the endpoint accepts it.
    [Fact]
    public async Task StreamOptionsIsRequestedOnTheFirstAttempt()
    {
        var handler = Sequenced(DoneSse);

        await Drain(Build(handler, null, "openai"));

        var body = TestJson.Element(Assert.Single(handler.RequestBodies));
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Theory]
    [InlineData("https://host", "/api/chat", "https://host/api/chat")]
    [InlineData("https://host/api", "/api/chat", "https://host/api/chat")]
    [InlineData("https://host/api/", "api/chat", "https://host/api/chat")]
    [InlineData("https://host/v1beta", "/v1beta/models/x:generateContent", "https://host/v1beta/models/x:generateContent")]
    [InlineData("https://host/x", "/v1/messages", "https://host/x/v1/messages")]
    public void CombineUrlMatchesTheLongestSharedPrefix(string baseUrl, string path, string expected)
        => Assert.Equal(expected, LlmHttp.CombineUrl(baseUrl, path));
}
