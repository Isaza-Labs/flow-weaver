using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Email;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Email;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// SMTP channel administration. Three things carry real consequences and are
// pinned here: the password follows the same three-state update rule as every
// other stored secret (null keeps, empty clears, value rotates) and is never
// echoed back; exactly one channel can be the default an email_send step falls
// back to; and a credential is never handed to an unencrypted public
// connection.
public class EmailChannelServiceTests
{
    private sealed class FakeSender : IEmailSender
    {
        public EmailSendResult Next { get; set; } = new(true, "<id@test>", "250 OK", null, 12);
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

        public EmailChannelService Build() => new(
            new EmailChannelRepository(Db), new FakeCrypto(), Sender, new FakeAudit());

        public Guid SeedChannel(
            string name = "relay", string provider = EmailChannel.ProviderSmtp,
            string host = "smtp.example.com", int port = 587,
            string security = EmailChannel.SecurityStartTls,
            string? username = "user@example.com", string? password = "s3cret",
            string from = "alerts@example.com",
            bool isDefault = false, bool enabled = true, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.EmailChannels.Add(new EmailChannel
            {
                EmailChannelId = id,
                Name = name,
                Provider = provider,
                Host = host,
                Port = port,
                Security = security,
                Username = username,
                EncryptedPassword = password is null ? null : Encoding.UTF8.GetBytes(password),
                FromAddress = from,
                IsDefault = isDefault,
                Enabled = enabled,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateEmailChannel Create(
        string name = "alerts", string provider = "gmail",
        string? host = null, int? port = null, string? security = null,
        string? username = "alerts@example.com", string? password = "app-password",
        string? from = "alerts@example.com", string? replyTo = null,
        bool? isDefault = null, bool? allowPrivate = null)
        => new()
        {
            Name = name,
            Provider = provider,
            Host = host,
            Port = port,
            Security = security,
            Username = username,
            Password = password,
            FromAddress = from,
            ReplyTo = replyTo,
            IsDefault = isDefault,
            AllowPrivateNetwork = allowPrivate,
        };

    private static EmailChannelResponse Ok(ActionResult<EmailChannelResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<EmailChannelResponse>(result.Value);
    }

    // ─── presets ────────────────────────────────────────────────────────

    [Fact]
    public void EveryProviderPresetIsExposedWithItsConnectionDefaults()
    {
        using var f = new Fixture();

        var result = f.Build().ListPresets();
        var payload = Assert.IsType<ListResponse<EmailProviderPresetResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(EmailProviderPreset.All.Count, payload.Data.Count);
        var gmail = payload.Data.Single(p => p.Provider == "gmail");
        Assert.Equal("smtp.gmail.com", gmail.Host);
        Assert.Equal(587, gmail.Port);
        Assert.Equal(EmailChannel.SecurityStartTls, gmail.Security);
        // SendGrid's username is not a choice — the UI locks the field to it.
        Assert.Equal("apikey", payload.Data.Single(p => p.Provider == "sendgrid").FixedUsername);
    }

    // ─── create ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gmail", "smtp.gmail.com", 587)]
    [InlineData("outlook365", "smtp.office365.com", 587)]
    [InlineData("sendgrid", "smtp.sendgrid.net", 587)]
    [InlineData("mailgun", "smtp.mailgun.org", 587)]
    public async Task ChoosingAProviderFillsInItsConnectionPreset(
        string provider, string expectedHost, int expectedPort)
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(provider: provider)));

        Assert.Equal(provider, body.Provider);
        Assert.Equal(expectedHost, body.Host);
        Assert.Equal(expectedPort, body.Port);
        Assert.Equal(EmailChannel.SecurityStartTls, body.Security);
    }

    // The preset is a default, not a lock: a Gmail account reached through a
    // corporate relay must keep the operator's host.
    [Fact]
    public async Task AnExplicitHostAndPortWinOverThePreset()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(
            Create(provider: "gmail", host: "relay.corp.example.com", port: 465, security: "ssl")));

        Assert.Equal("relay.corp.example.com", body.Host);
        Assert.Equal(465, body.Port);
        Assert.Equal(EmailChannel.SecuritySsl, body.Security);
    }

    [Fact]
    public async Task SendGridForcesItsMandatoryUsername()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(
            Create(provider: "sendgrid", username: "someone@example.com")));

        Assert.Equal("apikey", body.Username);
    }

    [Theory]
    [InlineData("postmark")]
    [InlineData("")]
    public async Task AnUnknownProviderIsRejected(string provider)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(provider: provider)));

        Assert.Equal("invalid_provider", ex.Code);
        Assert.Empty(f.Db.EmailChannels);
    }

    [Fact]
    public async Task ADuplicateNameIsRejected()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "alerts");

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(name: "alerts")));

        Assert.Equal("duplicate_name", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("missing@tld")]
    public async Task AMissingOrMalformedFromAddressIsRejected(string? from)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(from: from)));

        Assert.Contains("from_address", ex.Code);
        Assert.Empty(f.Db.EmailChannels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task APortOutsideTheValidRangeIsRejected(int port)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(port: port)));

        Assert.Equal("invalid_port", ex.Code);
    }

    [Fact]
    public async Task AnUnknownSecurityModeIsRejected()
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(security: "tls-maybe")));

        Assert.Equal("invalid_security", ex.Code);
    }

    [Fact]
    public async Task AUsernameWithoutAPasswordIsRejected()
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(username: "user@example.com", password: null)));

        Assert.Equal("password_required", ex.Code);
    }

    // An unauthenticated internal relay is a real deployment, so no credential
    // at all is allowed — it is the credential-over-plaintext pair that is not.
    [Fact]
    public async Task AnUnauthenticatedRelayNeedsNoCredential()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(
            provider: "smtp", host: "relay.internal", port: 25, security: "none",
            username: null, password: null)));

        Assert.Null(body.Username);
        Assert.False(body.HasPassword);
    }

    [Fact]
    public async Task SendingACredentialOverAnUnencryptedPublicConnectionIsRefused()
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(
                provider: "smtp", host: "smtp.example.com", port: 25, security: "none",
                username: "user@example.com", password: "s3cret")));

        Assert.Equal("plaintext_auth_refused", ex.Code);
    }

    // …but an operator who deliberately points at their own LAN relay says so
    // with allow_private_network, and then it is their call.
    [Fact]
    public async Task PlaintextAuthIsAllowedOnAnOptedInPrivateRelay()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(
            provider: "smtp", host: "10.0.0.5", port: 25, security: "none",
            username: "user@example.com", password: "s3cret", allowPrivate: true)));

        Assert.True(body.AllowPrivateNetwork);
        Assert.True(body.HasPassword);
    }

    // ─── secrets ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePasswordIsEncryptedAndNeverReturned()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(password: "app-password")));

        Assert.True(body.HasPassword);
        var stored = f.Db.EmailChannels.Single();
        Assert.Equal("app-password", Encoding.UTF8.GetString(stored.EncryptedPassword!));
        // The response type has no password member at all — has_password is the
        // only signal the UI gets.
        Assert.Null(typeof(EmailChannelResponse).GetProperty("Password"));
    }

    [Fact]
    public async Task AnOmittedPasswordOnUpdateKeepsTheStoredOne()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(password: "original");

        Ok(await f.Build().UpdateAsync(id, new UpdateEmailChannel { FromName = "Ops" }));

        Assert.Equal("original", Encoding.UTF8.GetString(f.Db.EmailChannels.Single().EncryptedPassword!));
    }

    [Fact]
    public async Task ANonEmptyPasswordOnUpdateRotatesIt()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(password: "original");

        Ok(await f.Build().UpdateAsync(id, new UpdateEmailChannel { Password = "rotated" }));

        Assert.Equal("rotated", Encoding.UTF8.GetString(f.Db.EmailChannels.Single().EncryptedPassword!));
    }

    // Clearing the password only makes sense together with clearing the
    // username, otherwise the merged row would authenticate with nothing.
    [Fact]
    public async Task AnEmptyPasswordOnUpdateClearsItAlongWithTheUsername()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(password: "original");

        var body = Ok(await f.Build().UpdateAsync(
            id, new UpdateEmailChannel { Username = string.Empty, Password = string.Empty }));

        Assert.False(body.HasPassword);
        Assert.Null(body.Username);
        Assert.Null(f.Db.EmailChannels.Single().EncryptedPassword);
    }

    [Fact]
    public async Task ClearingOnlyThePasswordWhileAUsernameRemainsIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(username: "user@example.com", password: "original");

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().UpdateAsync(id, new UpdateEmailChannel { Password = string.Empty }));

        Assert.Equal("password_required", ex.Code);
    }

    // ─── the default channel ────────────────────────────────────────────

    [Fact]
    public async Task PromotingANewDefaultDemotesThePreviousOne()
    {
        using var f = new Fixture();
        var first = f.SeedChannel(name: "old-default", isDefault: true);

        var body = Ok(await f.Build().CreateAsync(Create(name: "new-default", isDefault: true)));

        Assert.True(body.IsDefault);
        Assert.False(f.Db.EmailChannels.Single(c => c.EmailChannelId == first).IsDefault);
        Assert.Single(f.Db.EmailChannels, c => c.IsDefault);
    }

    [Fact]
    public async Task PromotingAnExistingChannelToDefaultDemotesThePreviousOne()
    {
        using var f = new Fixture();
        var first = f.SeedChannel(name: "old-default", isDefault: true);
        var second = f.SeedChannel(name: "other");

        Ok(await f.Build().UpdateAsync(second, new UpdateEmailChannel { IsDefault = true }));

        Assert.False(f.Db.EmailChannels.Single(c => c.EmailChannelId == first).IsDefault);
        Assert.True(f.Db.EmailChannels.Single(c => c.EmailChannelId == second).IsDefault);
    }

    // A soft-deleted row must stop being the fallback, or every email_send
    // step without an explicit channel keeps resolving to a dead relay.
    [Fact]
    public async Task DeletingTheDefaultChannelClearsTheFlag()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(isDefault: true);

        Assert.IsType<NoContentResult>(await f.Build().DeleteAsync(id));

        var row = f.Db.EmailChannels.Single();
        Assert.False(row.IsActive);
        Assert.False(row.Enabled);
        Assert.False(row.IsDefault);
    }

    [Fact]
    public async Task DeletingAnUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().DeleteAsync(Guid.NewGuid()));
    }

    // ─── test send ──────────────────────────────────────────────────────

    [Fact]
    public async Task ATestSendUsesTheChannelAndRecordsTheOutcome()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        var result = await f.Build().TestAsync(id, new TestEmailChannelRequest { To = "me@example.com" });
        var body = Assert.IsType<TestEmailChannelResponse>(result.Value);

        Assert.True(body.Ok);
        Assert.Equal("<id@test>", body.MessageId);
        var (channel, message) = Assert.Single(f.Sender.Sent);
        Assert.Equal(id, channel.EmailChannelId);
        Assert.Equal(new[] { "me@example.com" }, message.To);
        Assert.Equal("FlowWeaver test message", message.Subject);
        Assert.Equal("ok", f.Db.EmailChannels.Single().LastSendStatus);
    }

    // A failed test is a recorded outcome, not an exception: the operator needs
    // the SMTP server's own words back in the dialog.
    [Fact]
    public async Task AFailedTestSendIsReportedAndRecorded()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();
        f.Sender.Next = EmailSendResult.Failure("SMTP authentication failed: 535", 40);

        var result = await f.Build().TestAsync(id, new TestEmailChannelRequest { To = "me@example.com" });
        var body = Assert.IsType<TestEmailChannelResponse>(result.Value);

        Assert.False(body.Ok);
        Assert.Contains("535", body.Error);
        Assert.Equal("failed", f.Db.EmailChannels.Single().LastSendStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task ATestSendToAMalformedAddressIsRejectedBeforeConnecting(string to)
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().TestAsync(id, new TestEmailChannelRequest { To = to }));

        Assert.Equal("invalid_recipient", ex.Code);
        Assert.Empty(f.Sender.Sent);
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListingSkipsSoftDeletedChannels()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "live");
        f.SeedChannel(name: "gone", active: false);

        var result = await f.Build().ListAsync(50, 0);
        var payload = Assert.IsType<ListResponse<EmailChannelResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal("live", Assert.Single(payload.Data).Name);
        Assert.Equal(1, payload.Total);
    }

    [Fact]
    public async Task GettingAnUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().GetAsync(Guid.NewGuid()));
    }
}
