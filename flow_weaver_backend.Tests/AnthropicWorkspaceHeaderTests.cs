using System.Net;
using System.Net.Http.Headers;
using flow_weaver_backend.Services.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// An identity-linked API key belongs to a person who may have access to several
// workspaces, so the key alone does not say which one a request acts in. Anthropic
// answers 400 "anthropic-workspace-id is required when authenticating with an
// identity-linked API key" until the header names one, and no amount of looking at
// the prompt, the tools or the model explains it.
//
// A plain workspace-scoped key carries its own workspace, so the header must stay
// optional: sending an empty one would break every deployment that has no id to
// send.
public class AnthropicWorkspaceHeaderTests
{
    private static async Task<HttpRequestHeaders> SendAsync(string? workspaceId)
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn","usage":{}}""");
        var provider = new AnthropicProvider(
            new HttpClient(handler, disposeHandler: false), "sk-ant-test", null,
            NullLogger<AnthropicProvider>.Instance, workspaceId: workspaceId);

        await provider.ChatAsync(
            new() { new LlmMessage { Role = "user", Content = "hi" } },
            "claude-sonnet-5", 0.2, CancellationToken.None);

        return Assert.Single(handler.Requests).Headers;
    }

    [Fact]
    public async Task The_workspace_id_is_sent_when_configured()
    {
        var headers = await SendAsync("wrkspc_01ABC");

        Assert.True(headers.TryGetValues("anthropic-workspace-id", out var values),
            "an identity-linked key is rejected until the request names its workspace");
        Assert.Equal("wrkspc_01ABC", Assert.Single(values));
    }

    [Fact]
    public async Task The_header_is_absent_when_no_workspace_is_configured()
    {
        var headers = await SendAsync(null);

        Assert.False(headers.Contains("anthropic-workspace-id"),
            "a workspace-scoped key carries its own workspace; an empty header would be a 400");
    }

    // The field is optional and free-form, so blank is what a half-filled one
    // looks like by the time it reaches the provider.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_workspace_id_is_treated_as_unset(string blank)
    {
        var headers = await SendAsync(blank);

        Assert.False(headers.Contains("anthropic-workspace-id"));
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed()
    {
        var headers = await SendAsync("  wrkspc_01ABC	");

        Assert.Equal("wrkspc_01ABC", Assert.Single(headers.GetValues("anthropic-workspace-id")));
    }
}
