using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// Three boundaries where getting it wrong is not a bug report, it is an
// incident: the secret scrubber that runs before anything is persisted or sent
// to an LLM, the WhatsApp webhook's signature check, and the exception
// translator that decides what an error response says.
public class SecurityBatchTests
{

    // ─── SecretRedactor ─────────────────────────────────────────────────

    private static (string Redacted, bool Matched) Redact(string? input)
        => new SecretRedactor().Redact(input);

    [Theory]
    [InlineData("sk-ant-api03-" + "abcdefghij0123456789ABCDEFGHIJ0123456789", "anthropic_key")]
    [InlineData("sk-proj-abcdefghij0123456789ABCDEFGHIJ01", "openai_key")]
    [InlineData("ghp_abcdefghij0123456789ABCDEFGHIJ0123456789", "github_pat")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "aws_access_key")]
    [InlineData("xoxb-1234567890-abcdefghijklmno", "slack_token")]
    [InlineData("sk_live_abcdefghij0123456789ABCD", "stripe_key")]
    public void Redact_ProviderShapedKeysAreCaught(string secret, string label)
    {
        var (redacted, matched) = Redact($"the key is {secret} ok");

        Assert.True(matched);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains($"[REDACTED:{label}]", redacted);
    }

    [Fact]
    public void Redact_AJwtIsCaught()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        var (redacted, matched) = Redact($"Cookie: token={jwt}");

        Assert.True(matched);
        Assert.DoesNotContain(jwt, redacted);
    }

    [Theory]
    [InlineData("password = \"hunter2xyz\"")]
    [InlineData("passwd: hunter2xyz")]
    [InlineData("PWD=hunter2xyz")]
    public void Redact_ConfigStylePasswordPastesAreCaught(string line)
    {
        var (redacted, matched) = Redact(line);

        Assert.True(matched);
        Assert.DoesNotContain("hunter2xyz", redacted);
    }

    [Theory]
    [InlineData("api_key = abcdefghij0123456789")]
    [InlineData("API-KEY: abcdefghij0123456789")]
    [InlineData("token=abcdefghij0123456789")]
    [InlineData("secret: abcdefghij0123456789")]
    public void Redact_KeyValueTokensAreCaught(string line)
    {
        var (redacted, matched) = Redact(line);

        Assert.True(matched);
        Assert.DoesNotContain("abcdefghij0123456789", redacted);
    }

    [Fact]
    public void Redact_AuthorizationHeadersAreCaught()
    {
        var (redacted, matched) = Redact("Authorization: Bearer abcdefghij0123456789ABCDEF");

        Assert.True(matched);
        Assert.DoesNotContain("abcdefghij0123456789ABCDEF", redacted);
    }

    [Fact]
    public void Redact_BasicAuthIsCaught()
    {
        var (redacted, matched) = Redact("basic dXNlcjpwYXNzd29yZA==");

        Assert.True(matched);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZA", redacted);
    }

    // The last-resort pattern: an unlabelled 40+ hex blob is redacted even
    // though it might be a commit sha. Over-eager on purpose — a false
    // positive costs a log line, a miss costs a leaked key.
    [Fact]
    public void Redact_LongOpaqueHexIsRedactedByDesign()
    {
        var hex = new string('a', 40);

        var (redacted, matched) = Redact($"digest {hex}");

        Assert.True(matched);
        Assert.Contains("[REDACTED:hex_40_plus]", redacted);
    }

    // Ordinary operational text must survive untouched, otherwise every log
    // line becomes unreadable.
    [Theory]
    [InlineData("ssh admin@10.0.0.1 'show version'")]
    [InlineData("workflow lldp-sync completed in 4.2s")]
    [InlineData("interface GigabitEthernet0/1 is up")]
    [InlineData("")]
    public void Redact_OrdinaryTextIsLeftAlone(string input)
    {
        var (redacted, matched) = Redact(input);

        Assert.False(matched);
        Assert.Equal(input, redacted);
    }

    [Fact]
    public void Redact_NullBecomesTheEmptyString()
    {
        var (redacted, matched) = Redact(null);

        Assert.Equal(string.Empty, redacted);
        Assert.False(matched);
    }

    // Several distinct secrets in one blob are all removed, not just the
    // first match.
    [Fact]
    public void Redact_EverySecretInABlobIsRemoved()
    {
        var text = "key=sk-proj-abcdefghij0123456789ABCDEFGHIJ01 and AKIAIOSFODNN7EXAMPLE";

        var (redacted, matched) = Redact(text);

        Assert.True(matched);
        Assert.DoesNotContain("sk-proj-", redacted);
        Assert.DoesNotContain("AKIA", redacted);
    }

    // Redaction is idempotent: running it twice must not mangle the markers.
    [Fact]
    public void Redact_IsIdempotent()
    {
        var once = Redact("token=abcdefghij0123456789").Redacted;

        Assert.Equal(once, Redact(once).Redacted);
    }

    // ─── DomainExceptionHandler ─────────────────────────────────────────

    private static DomainExceptionHandler Handler()
        => new(NullLogger<DomainExceptionHandler>.Instance,
               new DefaultProblemDetailsFactory());

    // The framework's factory needs options plumbing; this stand-in produces
    // the same shape the handler then decorates.
    private sealed class DefaultProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null)
            => new()
            {
                Status = statusCode,
                Title = title,
                Type = type,
                Detail = detail,
                Instance = instance,
            };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary,
            int? statusCode = null, string? title = null, string? type = null,
            string? detail = null, string? instance = null)
            => throw new NotSupportedException();
    }

    private static async Task<(bool Handled, int Status, JsonElement Body)> Handle(Exception ex)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/workflow";
        var body = new MemoryStream();
        ctx.Response.Body = body;

        var handled = await Handler().TryHandleAsync(ctx, ex, default);

        body.Position = 0;
        var json = body.Length == 0
            ? TestJson.Element("null")
            : JsonDocument.Parse(body.ToArray()).RootElement.Clone();
        return (handled, ctx.Response.StatusCode, json);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(413)]
    public async Task DomainErrors_MapToTheirStatusCode(int status)
    {
        Exception ex = status switch
        {
            400 => new ValidationException("bad", "bad_code"),
            401 => new UnauthorizedException("nope"),
            403 => new ForbiddenException("nope"),
            404 => new NotFoundException("workflow", Guid.NewGuid()),
            409 => new ConflictException("clash"),
            412 => new PreconditionFailedException("stale"),
            _ => new PayloadTooLargeException("too big"),
        };

        var (handled, code, _) = await Handle(ex);

        Assert.True(handled);
        Assert.Equal(status, code);
    }

    // Both response shapes live in the same object: the legacy frontend reads
    // `error`, newer clients pivot on `code`.
    [Fact]
    public async Task DomainErrors_CarryBothTheLegacyErrorAndTheStableCode()
    {
        var (_, _, body) = await Handle(new ConflictException("slug already in use", "slug_taken"));

        Assert.Equal("slug already in use", body.GetProperty("error").GetString());
        Assert.Equal("slug_taken", body.GetProperty("code").GetString());
        Assert.Equal("slug already in use", body.GetProperty("detail").GetString());
        Assert.Equal("Conflict", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DomainErrors_UseTheProblemJsonContentType()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await Handler().TryHandleAsync(ctx, new ConflictException("x"), default);

        Assert.Equal("application/problem+json", ctx.Response.ContentType);
    }

    // A validation error can carry a per-field list; the import wizard's
    // commit-failed toast reads exactly that key.
    [Fact]
    public async Task DomainErrors_ValidationDetailsRideAlong()
    {
        var (_, _, body) = await Handle(new ValidationException(
            "schema failed", "schema_invalid", new[] { "nodes[0].x: required" }));

        Assert.Equal("nodes[0].x: required",
            body.GetProperty("details").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task DomainErrors_AnEmptyDetailListIsOmitted()
    {
        var (_, _, body) = await Handle(new ValidationException("bad", "bad_code"));

        Assert.False(body.TryGetProperty("details", out _));
    }

    // Anything that isn't a DomainException falls through untouched — the
    // framework turns it into a generic 500 rather than leaking internals.
    [Fact]
    public async Task DomainErrors_UnknownExceptionsAreNotHandled()
    {
        var (handled, _, body) = await Handle(new InvalidOperationException("internal detail"));

        Assert.False(handled);
        Assert.Equal(JsonValueKind.Null, body.ValueKind);
    }

    // ─── WhatsAppProvider ───────────────────────────────────────────────

    private const string AppSecret = "meta-app-secret";

    private static WhatsAppProvider WhatsApp(FakeHttpMessageHandler? http = null)
        => new(new FakeHttpClientFactory(http ?? new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")),
               NullLogger<WhatsAppProvider>.Instance);

    private static MessagingChannel Channel(
        bool allowUnsigned = false, string? verifyToken = "verify-me",
        string? phoneNumberId = "PN1", string? graphVersion = null)
    {
        var config = new Dictionary<string, object?>();
        if (verifyToken is not null) config["verify_token"] = verifyToken;
        if (phoneNumberId is not null) config["phone_number_id"] = phoneNumberId;
        if (graphVersion is not null) config["graph_version"] = graphVersion;

        return new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(),
            Name = "wa",
            Provider = MessagingChannel.ProviderWhatsApp,
            AllowUnsigned = allowUnsigned,
            ExternalConfig = TestJson.Element(JsonSerializer.Serialize(config)),
            Enabled = true,
            IsActive = true,
        };
    }

    private static MessagingHttpRequest Get(params (string Key, string Value)[] query)
        => new()
        {
            Method = "GET",
            Body = Array.Empty<byte>(),
            Query = query.ToDictionary(q => q.Key, q => q.Value, StringComparer.OrdinalIgnoreCase),
        };

    private static MessagingHttpRequest Post(string body, string? signature = null, bool sign = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sign)
        {
            headers["X-Hub-Signature-256"] = signature ?? "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), bytes)).ToLowerInvariant();
        }
        return new MessagingHttpRequest { Method = "POST", Body = bytes, Headers = headers };
    }

    // Meta's subscription handshake: only echo the challenge when the token
    // the caller supplied matches the one the admin configured.
    [Fact]
    public async Task WhatsApp_TheSubscriptionHandshakeEchoesTheChallenge()
    {
        var result = await WhatsApp().VerifyAsync(
            Channel(),
            Get(("hub.mode", "subscribe"), ("hub.verify_token", "verify-me"), ("hub.challenge", "42")),
            AppSecret, default);

        Assert.Equal(WebhookVerifyOutcome.Challenge, result.Outcome);
        Assert.Equal("42", result.ChallengeBody);
    }

    [Theory]
    [InlineData("subscribe", "wrong-token")]
    [InlineData("unsubscribe", "verify-me")]
    public async Task WhatsApp_AMismatchedHandshakeIsRejected(string mode, string token)
    {
        var result = await WhatsApp().VerifyAsync(
            Channel(),
            Get(("hub.mode", mode), ("hub.verify_token", token), ("hub.challenge", "42")),
            AppSecret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Equal(403, result.RejectStatusCode);
    }

    // Without a configured verify token there is nothing to compare against,
    // so the handshake can't be completed.
    [Fact]
    public async Task WhatsApp_AHandshakeWithoutAConfiguredTokenIsRejected()
    {
        var result = await WhatsApp().VerifyAsync(
            Channel(verifyToken: null),
            Get(("hub.mode", "subscribe"), ("hub.verify_token", "anything"), ("hub.challenge", "42")),
            AppSecret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task WhatsApp_AValidPostSignatureIsAccepted()
    {
        var result = await WhatsApp().VerifyAsync(Channel(), Post("{}"), AppSecret, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task WhatsApp_AWrongPostSignatureIsRejected()
    {
        var result = await WhatsApp().VerifyAsync(
            Channel(), Post("{}", signature: "sha256=deadbeef"), AppSecret, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("signature mismatch", result.RejectReason);
    }

    [Fact]
    public async Task WhatsApp_AMissingSignatureHeaderIsRejected()
    {
        var result = await WhatsApp().VerifyAsync(Channel(), Post("{}", sign: false), AppSecret, default);

        Assert.Contains("missing X-Hub-Signature-256", result.RejectReason);
    }

    [Fact]
    public async Task WhatsApp_NoAppSecretAndNoOptInIsRejected()
    {
        var result = await WhatsApp().VerifyAsync(Channel(), Post("{}", sign: false), null, default);

        Assert.Equal(WebhookVerifyOutcome.Rejected, result.Outcome);
        Assert.Contains("allow_unsigned", result.RejectReason);
    }

    [Fact]
    public async Task WhatsApp_AllowUnsignedIsTheAdminsExplicitOptOut()
    {
        var result = await WhatsApp().VerifyAsync(
            Channel(allowUnsigned: true), Post("{}", sign: false), null, default);

        Assert.Equal(WebhookVerifyOutcome.Verified, result.Outcome);
    }

    private static InboundMessage? ParseWhatsApp(string body)
        => WhatsApp().ParseInbound(Channel(), new MessagingHttpRequest
        {
            Method = "POST",
            Body = Encoding.UTF8.GetBytes(body),
        });

    private static string TextEvent(
        string from = "34600111222", string id = "wamid.1", string text = "hola", string type = "text")
        => JsonSerializer.Serialize(new
        {
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                metadata = new { phone_number_id = "PN1" },
                                messages = new[]
                                {
                                    new { type, from, id, text = new { body = text } },
                                },
                            },
                        },
                    },
                },
            },
        });

    [Fact]
    public void WhatsApp_ReadsATextMessage()
    {
        var msg = ParseWhatsApp(TextEvent());

        Assert.NotNull(msg);
        Assert.Equal("wamid.1", msg!.ProviderEventId);
        Assert.Equal("PN1", msg.ExternalWorkspaceId);
        Assert.Equal("34600111222", msg.ExternalUserId);
        // WhatsApp conversations are 1:1, so the thread IS the contact.
        Assert.Equal("34600111222", msg.ExternalThreadId);
        Assert.Equal("hola", msg.Text);
    }

    // Delivery receipts and media messages ride the same webhook; only text
    // is actionable today.
    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("sticker")]
    public void WhatsApp_NonTextMessagesAreIgnored(string type)
    {
        Assert.Null(ParseWhatsApp(TextEvent(type: type)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"entry":[]}""")]
    [InlineData("""{"entry":[{"changes":[]}]}""")]
    [InlineData("""{"entry":[{"changes":[{"value":{}}]}]}""")]
    public void WhatsApp_NonMessagePayloadsYieldNothing(string body)
    {
        Assert.Null(ParseWhatsApp(body));
    }

    [Theory]
    [InlineData("", "wamid.1")]
    [InlineData("34600111222", "")]
    public void WhatsApp_AnIncompleteMessageIsDropped(string from, string id)
    {
        Assert.Null(ParseWhatsApp(TextEvent(from: from, id: id)));
    }

    [Fact]
    public async Task WhatsApp_SendPostsToTheGraphApiWithTheAccessToken()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        await WhatsApp(http).SendAsync(
            Channel(), "EAAG-token",
            new OutboundMessage { ExternalThreadId = "34600111222", Text = "listo" }, default);

        var request = http.Requests[^1];
        Assert.Contains("/PN1/messages", request.RequestUri!.AbsoluteUri);
        Assert.Equal("EAAG-token", request.Headers.Authorization!.Parameter);
        Assert.Contains("\"to\":\"34600111222\"", http.RequestBodies[^1]);
    }

    [Fact]
    public async Task WhatsApp_TheGraphVersionIsConfigurable()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        await WhatsApp(http).SendAsync(
            Channel(graphVersion: "v18.0"), "tok",
            new OutboundMessage { ExternalThreadId = "34600111222", Text = "x" }, default);

        Assert.Contains("/v18.0/", http.Requests[^1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task WhatsApp_SendWithoutATokenIsRefusedBeforeAnyCall()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WhatsApp(http).SendAsync(
            Channel(), null,
            new OutboundMessage { ExternalThreadId = "x", Text = "y" }, default));

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task WhatsApp_SendWithoutAPhoneNumberIdIsRefused()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WhatsApp(http).SendAsync(
            Channel(phoneNumberId: null), "tok",
            new OutboundMessage { ExternalThreadId = "x", Text = "y" }, default));

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task WhatsApp_AnHttpErrorIsAFailure()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, """{"error":{"message":"bad"}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => WhatsApp(http).SendAsync(
            Channel(), "tok",
            new OutboundMessage { ExternalThreadId = "x", Text = "y" }, default));
    }

    // WhatsApp's text body caps at 4096; a longer agent answer is truncated
    // rather than rejected by the API.
    [Fact]
    public async Task WhatsApp_TheTextIsTruncatedToTheApiLimit()
    {
        var http = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");

        await WhatsApp(http).SendAsync(
            Channel(), "tok",
            new OutboundMessage { ExternalThreadId = "x", Text = new string('y', 9000) }, default);

        using var doc = JsonDocument.Parse(http.RequestBodies[^1]);
        Assert.True(doc.RootElement.GetProperty("text").GetProperty("body").GetString()!.Length <= 4096);
    }

    // ─── AllowedPythonModuleRepository (read paths) ─────────────────────

    private static AllowedPythonModule Module(
        string importName, string status = "ready", bool active = true)
        => new()
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            ImportName = importName,
            PipSpec = importName,
            Source = AllowedPythonModule.SourcePip,
            Status = status,
            IsActive = active,
        };

    [Fact]
    public async Task PythonModules_OnlyReadyImportsAreExposedToTheSandbox()
    {
        using var db = TestDb.NewContext();
        db.AllowedPythonModules.AddRange(
            Module("netmiko", AllowedPythonModule.StatusReady),
            Module("pandas", AllowedPythonModule.StatusPending),
            Module("broken", AllowedPythonModule.StatusFailed));
        db.SaveChanges();

        var names = await new AllowedPythonModuleRepository(db).ListReadyImportNamesAsync();

        Assert.Equal(new[] { "netmiko" }, names);
    }

    [Fact]
    public async Task PythonModules_ASoftDeletedEntryIsNoLongerAllowed()
    {
        using var db = TestDb.NewContext();
        db.AllowedPythonModules.Add(Module("netmiko", AllowedPythonModule.StatusReady, active: false));
        db.SaveChanges();

        var repo = new AllowedPythonModuleRepository(db);
        Assert.Empty(await repo.ListReadyImportNamesAsync());
        Assert.Null(await repo.FindByImportNameAsync("netmiko"));
    }

    [Fact]
    public async Task PythonModules_LookupByImportNameIsExact()
    {
        using var db = TestDb.NewContext();
        db.AllowedPythonModules.Add(Module("netmiko"));
        db.SaveChanges();
        var repo = new AllowedPythonModuleRepository(db);

        Assert.NotNull(await repo.FindByImportNameAsync("netmiko"));
        Assert.Null(await repo.FindByImportNameAsync("netmik"));
    }
}
