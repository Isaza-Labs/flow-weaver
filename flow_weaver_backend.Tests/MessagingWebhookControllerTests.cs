using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Services.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// The public messaging webhook entry point. It owns no policy of its own — the
// receiver decides everything — but it does own two shapes the providers care
// about: a verification challenge must be echoed VERBATIM (Slack and WhatsApp
// both reject a JSON-wrapped answer and refuse to enable the webhook), and the
// raw request has to reach the receiver intact so the signature check works.
public class MessagingWebhookControllerTests
{
    private sealed class RecordingIngest : IMessagingIngestService
    {
        public MessagingIngestOutcome Outcome { get; set; } = new(202, "accepted");
        public MessagingHttpRequest? LastRequest { get; private set; }
        public string? LastProvider { get; private set; }
        public Guid LastChannelId { get; private set; }

        public Task<MessagingIngestOutcome> ReceiveAsync(
            string provider, Guid channelId, MessagingHttpRequest request, CancellationToken ct)
        {
            LastProvider = provider;
            LastChannelId = channelId;
            LastRequest = request;
            return Task.FromResult(Outcome);
        }

        public Task<MessagingIngestOutcome> ReceiveVerifiedAsync(
            string provider, Guid channelId, byte[] body, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static (MessagingWebhookController Controller, RecordingIngest Ingest, DefaultHttpContext Http)
        Build(string method = "POST", string body = "{}")
    {
        var ingest = new RecordingIngest();
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var controller = new MessagingWebhookController(ingest)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, ingest, http);
    }

    [Fact]
    public async Task TheProviderAndChannelFromTheRouteReachTheReceiver()
    {
        var (controller, ingest, _) = Build();
        var channelId = Guid.NewGuid();

        await controller.Ingest("slack", channelId, default);

        Assert.Equal("slack", ingest.LastProvider);
        Assert.Equal(channelId, ingest.LastChannelId);
    }

    // The signature check runs over the exact bytes, so the body must arrive
    // unmodified.
    [Fact]
    public async Task ThePostBodyReachesTheReceiverByteForByte()
    {
        const string payload = """{"type":"event_callback","event":{"text":"hola"}}""";
        var (controller, ingest, _) = Build(body: payload);

        await controller.Ingest("slack", Guid.NewGuid(), default);

        Assert.Equal(payload, Encoding.UTF8.GetString(ingest.LastRequest!.Body));
        Assert.Equal("POST", ingest.LastRequest.Method);
    }

    [Fact]
    public async Task HeadersAndQueryReachTheReceiver()
    {
        var (controller, ingest, http) = Build();
        http.Request.Headers["X-Slack-Signature"] = "v0=abc";
        http.Request.QueryString = new QueryString("?hub.challenge=42");

        await controller.Ingest("whatsapp", Guid.NewGuid(), default);

        Assert.Equal("v0=abc", ingest.LastRequest!.Header("X-Slack-Signature"));
        Assert.Equal("42", ingest.LastRequest.QueryValue("hub.challenge"));
    }

    // Header lookup is case-insensitive — providers are inconsistent about
    // casing and the verifiers look them up by canonical name.
    [Fact]
    public async Task HeaderLookupIsCaseInsensitive()
    {
        var (controller, ingest, http) = Build();
        http.Request.Headers["x-slack-signature"] = "v0=abc";

        await controller.Ingest("slack", Guid.NewGuid(), default);

        Assert.Equal("v0=abc", ingest.LastRequest!.Header("X-Slack-Signature"));
    }

    // A GET is the subscription handshake; there is no body to read.
    [Fact]
    public async Task AGetCarriesNoBody()
    {
        var (controller, ingest, http) = Build(method: "GET", body: "ignored");
        http.Request.QueryString = new QueryString("?hub.mode=subscribe");

        await controller.Ingest("whatsapp", Guid.NewGuid(), default);

        Assert.Empty(ingest.LastRequest!.Body);
        Assert.Equal("GET", ingest.LastRequest.Method);
    }

    // The challenge is echoed verbatim: wrapping it in JSON makes the provider
    // refuse to enable the webhook at all.
    [Fact]
    public async Task AChallengeIsEchoedVerbatim()
    {
        var (controller, ingest, _) = Build();
        ingest.Outcome = new MessagingIngestOutcome(200, "abc123", "text/plain", Raw: true);

        var result = await controller.Ingest("slack", Guid.NewGuid(), default);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("abc123", content.Content);
        Assert.Equal("text/plain", content.ContentType);
    }

    [Fact]
    public async Task ARawOutcomeWithNoBodyEchoesTheEmptyString()
    {
        var (controller, ingest, _) = Build();
        ingest.Outcome = new MessagingIngestOutcome(200, null, "text/plain", Raw: true);

        var content = Assert.IsType<ContentResult>(await controller.Ingest("slack", Guid.NewGuid(), default));

        Assert.Equal(string.Empty, content.Content);
    }

    // Everything else is a small JSON status the provider ignores; `ok`
    // mirrors the status class so our own tooling can read it.
    [Theory]
    [InlineData(202, true)]
    [InlineData(200, true)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    public async Task ANonRawOutcomeBecomesAJsonStatus(int statusCode, bool expectedOk)
    {
        var (controller, ingest, _) = Build();
        ingest.Outcome = new MessagingIngestOutcome(statusCode, "message here");

        var result = await controller.Ingest("slack", Guid.NewGuid(), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(statusCode, obj.StatusCode);
        var json = JsonSerializer.SerializeToElement(obj.Value!);
        Assert.Equal(expectedOk, json.GetProperty("ok").GetBoolean());
        Assert.Equal("message here", json.GetProperty("message").GetString());
    }
}
