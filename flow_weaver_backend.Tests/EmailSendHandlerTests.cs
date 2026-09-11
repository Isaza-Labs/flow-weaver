using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Email;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The email_send step. Everything except the credential comes from the node's
// config, so the parsing rules (recipients as string OR array, base64
// attachments, either body shape) are the contract workflow authors write
// against. Channel resolution matters just as much: a step that names no
// channel must land on the default one, and must refuse rather than guess when
// there isn't one.
public class EmailSendHandlerTests
{
    private sealed class FakeSender : IEmailSender
    {
        public EmailSendResult Next { get; set; } = new(true, "<id@test>", "250 OK", null, 7);
        public List<(EmailChannel Channel, EmailMessage Message)> Sent { get; } = new();

        public Task<EmailSendResult> SendAsync(
            EmailChannel channel, EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add((channel, message));
            return Task.FromResult(Next);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeSender Sender { get; } = new();

        public EmailSendHandler Build() => new(
            new EmailChannelRepository(Db), Sender,
            NullLogger<EmailSendHandler>.Instance);

        public Guid SeedChannel(
            string name = "relay", bool isDefault = false, bool enabled = true, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.EmailChannels.Add(new EmailChannel
            {
                EmailChannelId = id,
                Name = name,
                Provider = EmailChannel.ProviderSmtp,
                Host = "smtp.example.com",
                Port = 587,
                Security = EmailChannel.SecurityStartTls,
                Username = "user@example.com",
                EncryptedPassword = Encoding.UTF8.GetBytes("s3cret"),
                FromAddress = "alerts@example.com",
                IsDefault = isDefault,
                Enabled = enabled,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static SnippetRequest Request(string json) => new()
    {
        StepRunId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "send-mail",
        SnippetId = Guid.NewGuid(),
        SnippetType = EmailSendHandler.SnippetType,
        InputPayload = TestJson.Element(json),
        DeviceId = null,
    };

    private const string MinimalPayload =
        """{"to":"ops@example.com","subject":"Hi","body":"there"}""";

    // ─── registration ───────────────────────────────────────────────────

    // A sent email has no undo. Promotion warns on this and rollback refuses a
    // graph containing one, so the floor must never drift.
    [Fact]
    public void TheHandlerRegistersAsANonReversibleEmailSendType()
    {
        using var f = new Fixture();
        var handler = f.Build();

        Assert.Equal("email_send", handler.Type);
        Assert.Equal(IdempotencyKind.NonReversible, handler.DefaultIdempotency);
    }

    // ─── channel resolution ─────────────────────────────────────────────

    [Fact]
    public async Task AStepWithNoChannelIdUsesTheDefaultChannel()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "other");
        var fallback = f.SeedChannel(name: "fallback", isDefault: true);

        var result = await f.Build().ExecuteAsync(Request(MinimalPayload), default);

        Assert.True(result.Success);
        Assert.Equal(fallback, Assert.Single(f.Sender.Sent).Channel.EmailChannelId);
    }

    [Fact]
    public async Task AnExplicitChannelIdWinsOverTheDefault()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "fallback", isDefault: true);
        var chosen = f.SeedChannel(name: "chosen");

        var result = await f.Build().ExecuteAsync(Request(
            $$"""{"channel_id":"{{chosen}}","to":"ops@example.com","subject":"Hi","body":"there"}"""),
            default);

        Assert.True(result.Success);
        Assert.Equal(chosen, Assert.Single(f.Sender.Sent).Channel.EmailChannelId);
    }

    // Guessing "the only channel" would silently send corporate mail through
    // whatever relay happened to be created first.
    [Fact]
    public async Task AStepWithNoChannelIdAndNoDefaultFailsWithAnActionableError()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "not-default");

        var result = await f.Build().ExecuteAsync(Request(MinimalPayload), default);

        Assert.False(result.Success);
        Assert.Contains("no default email channel", result.Error);
        Assert.Empty(f.Sender.Sent);
    }

    [Theory]
    [InlineData(false, true)]   // disabled
    [InlineData(true, false)]   // soft-deleted
    public async Task ADisabledOrDeletedChannelIsRefused(bool enabled, bool active)
    {
        using var f = new Fixture();
        var id = f.SeedChannel(enabled: enabled, active: active);

        var result = await f.Build().ExecuteAsync(Request(
            $$"""{"channel_id":"{{id}}","to":"ops@example.com","subject":"Hi","body":"there"}"""),
            default);

        Assert.False(result.Success);
        Assert.Contains("not found, disabled or deleted", result.Error);
        Assert.Empty(f.Sender.Sent);
    }

    [Fact]
    public async Task ANonGuidChannelIdIsRejectedWithTheOffendingValue()
    {
        using var f = new Fixture();

        var result = await f.Build().ExecuteAsync(Request(
            """{"channel_id":"the-gmail-one","to":"ops@example.com","subject":"Hi","body":"x"}"""),
            default);

        Assert.False(result.Success);
        Assert.Contains("the-gmail-one", result.Error);
    }

    // ─── payload shape ──────────────────────────────────────────────────

    // Hand-authored nodes write one address; a node fed from an upstream step
    // writes an array. Both are the normal case.
    [Theory]
    [InlineData("""{"to":"a@x.com","subject":"s","body":"b"}""", 1)]
    [InlineData("""{"to":"a@x.com, b@x.com","subject":"s","body":"b"}""", 2)]
    [InlineData("""{"to":["a@x.com","b@x.com"],"subject":"s","body":"b"}""", 2)]
    public void RecipientsAcceptAStringACommaListOrAnArray(string json, int expected)
    {
        var message = EmailSendHandler.BuildMessage(TestJson.Element(json));

        Assert.Equal(expected, message.To.Count);
        Assert.All(message.To, a => Assert.DoesNotContain(" ", a));
    }

    [Fact]
    public void CcAndBccAreParsedIndependentlyOfTo()
    {
        var message = EmailSendHandler.BuildMessage(TestJson.Element(
            """{"to":"a@x.com","cc":["c@x.com"],"bcc":"d@x.com","subject":"s","body":"b"}"""));

        Assert.Equal(new[] { "a@x.com" }, message.To);
        Assert.Equal(new[] { "c@x.com" }, message.Cc);
        Assert.Equal(new[] { "d@x.com" }, message.Bcc);
    }

    // A cc-only or bcc-only notification is legitimate.
    [Fact]
    public void ARecipientInCcAloneSatisfiesTheRequirement()
    {
        var message = EmailSendHandler.BuildMessage(TestJson.Element(
            """{"cc":"c@x.com","subject":"s","body":"b"}"""));

        Assert.Empty(message.To);
        Assert.Equal(new[] { "c@x.com" }, message.Cc);
    }

    [Theory]
    [InlineData("""{"subject":"s","body":"b"}""", "to is required")]
    [InlineData("""{"to":"a@x.com","body":"b"}""", "subject is required")]
    [InlineData("""{"to":"a@x.com","subject":"s"}""", "body or html is required")]
    public void AnIncompletePayloadIsRejectedByName(string json, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => EmailSendHandler.BuildMessage(TestJson.Element(json)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void HtmlAloneIsAValidBody()
    {
        var message = EmailSendHandler.BuildMessage(TestJson.Element(
            """{"to":"a@x.com","subject":"s","html":"<p>hi</p>"}"""));

        Assert.Null(message.TextBody);
        Assert.Equal("<p>hi</p>", message.HtmlBody);
    }

    [Fact]
    public void AttachmentsCarryTheirNameContentAndType()
    {
        var message = EmailSendHandler.BuildMessage(TestJson.Element(
            """
            {"to":"a@x.com","subject":"s","body":"b",
             "attachments":[{"file_name":"r.pdf","content_base64":"AAECAw==","content_type":"application/pdf"}]}
            """));

        var att = Assert.Single(message.Attachments);
        Assert.Equal("r.pdf", att.FileName);
        Assert.Equal("AAECAw==", att.ContentBase64);
        Assert.Equal("application/pdf", att.ContentType);
    }

    [Theory]
    [InlineData("""{"to":"a@x.com","subject":"s","body":"b","attachments":[{"content_base64":"AA=="}]}""")]
    [InlineData("""{"to":"a@x.com","subject":"s","body":"b","attachments":[{"file_name":"r.pdf"}]}""")]
    public void AnIncompleteAttachmentIsRejected(string json)
    {
        Assert.Throws<ArgumentException>(
            () => EmailSendHandler.BuildMessage(TestJson.Element(json)));
    }

    // python_snippet and slack_message both tolerate a nested `input` wrapper;
    // email_send has to as well or a graph mixing them breaks at one node.
    [Fact]
    public async Task ANestedInputObjectIsUnwrapped()
    {
        using var f = new Fixture();
        f.SeedChannel(isDefault: true);

        var result = await f.Build().ExecuteAsync(
            Request("{\"input\":" + MinimalPayload + "}"), default);

        Assert.True(result.Success);
        Assert.Equal("Hi", Assert.Single(f.Sender.Sent).Message.Subject);
    }

    // ─── result ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOutputCarriesTheChannelAndMessageIdForDownstreamSteps()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(name: "relay", isDefault: true);

        var result = await f.Build().ExecuteAsync(Request(
            """{"to":["a@x.com","b@x.com"],"cc":"c@x.com","subject":"s","body":"b"}"""), default);

        Assert.True(result.Success);
        var output = result.Output;
        Assert.True(output.GetProperty("ok").GetBoolean());
        Assert.Equal(id.ToString(), output.GetProperty("channel_id").GetString());
        Assert.Equal("relay", output.GetProperty("channel_name").GetString());
        Assert.Equal("<id@test>", output.GetProperty("message_id").GetString());
        Assert.Equal(3, output.GetProperty("recipients").GetInt32());
    }

    // Step logs land in traces, so the addresses must not — only how many.
    [Fact]
    public async Task TheStepLogRecordsTheRecipientCountNotTheAddresses()
    {
        using var f = new Fixture();
        f.SeedChannel(isDefault: true);

        var result = await f.Build().ExecuteAsync(Request(
            """{"to":"secret.person@x.com","subject":"s","body":"b"}"""), default);

        Assert.DoesNotContain("secret.person", result.Logs);
        Assert.Contains("1 recipient(s)", result.Logs);
    }

    [Fact]
    public async Task ASmtpFailureFailsTheStepAndSurfacesTheReason()
    {
        using var f = new Fixture();
        f.SeedChannel(isDefault: true);
        f.Sender.Next = EmailSendResult.Failure("SMTP authentication failed: 535", 30);

        var result = await f.Build().ExecuteAsync(Request(MinimalPayload), default);

        Assert.False(result.Success);
        Assert.Contains("535", result.Error);
        Assert.False(result.Output.GetProperty("ok").GetBoolean());
    }

    // ─── MIME construction ──────────────────────────────────────────────

    private static EmailChannel Channel(
        string from = "alerts@example.com", string? fromName = null, string? replyTo = null)
        => new()
        {
            EmailChannelId = Guid.NewGuid(),
            Name = "relay",
            Provider = EmailChannel.ProviderSmtp,
            Host = "smtp.example.com",
            Port = 587,
            Security = EmailChannel.SecurityStartTls,
            FromAddress = from,
            FromName = fromName,
            ReplyTo = replyTo,
        };

    [Fact]
    public void TheChannelSuppliesTheSenderWhenTheMessageDoesNot()
    {
        var mime = EmailSender.BuildMime(
            Channel(fromName: "FlowWeaver", replyTo: "noc@example.com"),
            new EmailMessage { To = new[] { "a@x.com" }, Subject = "s", TextBody = "b" });

        var from = Assert.Single(mime.From.Mailboxes);
        Assert.Equal("alerts@example.com", from.Address);
        Assert.Equal("FlowWeaver", from.Name);
        Assert.Equal("noc@example.com", Assert.Single(mime.ReplyTo.Mailboxes).Address);
    }

    [Fact]
    public void AMessageLevelSenderOverridesTheChannelDefault()
    {
        var mime = EmailSender.BuildMime(
            Channel(),
            new EmailMessage
            {
                To = new[] { "a@x.com" },
                Subject = "s",
                TextBody = "b",
                FromAddress = "other@example.com",
            });

        Assert.Equal("other@example.com", Assert.Single(mime.From.Mailboxes).Address);
    }

    // Many relays and spam filters penalise html-only mail, so the alternative
    // is generated rather than left to the workflow author.
    [Fact]
    public void AnHtmlOnlyMessageGetsAPlainTextAlternative()
    {
        var mime = EmailSender.BuildMime(
            Channel(),
            new EmailMessage
            {
                To = new[] { "a@x.com" },
                Subject = "s",
                HtmlBody = "<p>Backup <b>finished</b></p>",
            });

        Assert.NotNull(mime.TextBody);
        Assert.Contains("Backup finished", mime.TextBody);
        Assert.DoesNotContain("<b>", mime.TextBody);
    }

    [Fact]
    public void AChannelWithNoSenderAndNoOverrideIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailSender.BuildMime(
            Channel(from: string.Empty),
            new EmailMessage { To = new[] { "a@x.com" }, Subject = "s", TextBody = "b" }));

        Assert.Contains("from_address", ex.Message);
    }

    [Fact]
    public void AMessageWithNoRecipientAtAllIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailSender.BuildMime(
            Channel(), new EmailMessage { Subject = "s", TextBody = "b" }));

        Assert.Contains("at least one recipient", ex.Message);
    }

    [Fact]
    public void AnAttachmentWithInvalidBase64IsRejectedByName()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailSender.BuildMime(
            Channel(),
            new EmailMessage
            {
                To = new[] { "a@x.com" },
                Subject = "s",
                TextBody = "b",
                Attachments = new[] { new EmailAttachment("r.pdf", "not-base64!!", null) },
            }));

        Assert.Contains("r.pdf", ex.Message);
        Assert.Contains("base64", ex.Message);
    }

    // ─── transport security ─────────────────────────────────────────────

    // StartTlsWhenAvailable would let a MITM strip the STARTTLS advertisement
    // and get the password in the clear, so an unrecognised mode must fail
    // closed onto the strict option, never onto None.
    [Theory]
    [InlineData("starttls", SecureSocketOptions.StartTls)]
    [InlineData("ssl", SecureSocketOptions.SslOnConnect)]
    [InlineData("none", SecureSocketOptions.None)]
    [InlineData("something-else", SecureSocketOptions.StartTls)]
    [InlineData(null, SecureSocketOptions.StartTls)]
    public void TheSecurityModeMapsToTheStrictSocketOption(string? security, SecureSocketOptions expected)
    {
        Assert.Equal(expected, EmailSender.SocketOptions(security));
    }
}
