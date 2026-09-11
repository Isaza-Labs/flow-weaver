using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// Channel administration. The three secrets (bot token, signing secret, app
// token) all follow the same three-state rule on update — null leaves it
// alone, empty clears it, anything else rotates — and none of them is ever
// echoed back. MaxRole is the channel's privilege ceiling, so an unknown value
// must be refused rather than stored and silently ignored at dispatch time.
public class MessagingChannelServiceWriteTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public MessagingChannelService Build() => new(
            new MessagingChannelRepository(Db),
            new MessagingDeliveryRepository(Db),
            new MessagingIdentityLinkRepository(Db),
            new FakeCrypto(),
            new FakeUser(),
            Options.Create(new MessagingOptions()),
            new FakeAudit());

        public Guid SeedChannel(
            string provider = "telegram", string name = "chan",
            string? botToken = "bot-token", string? signingSecret = "sign-secret",
            string? maxRole = null, bool active = true)
        {
            var id = Guid.NewGuid();
            Db.MessagingChannels.Add(new MessagingChannel
            {
                MessagingChannelId = id,
                Provider = provider,
                Name = name,
                EncryptedBotToken = botToken is null ? null : Encoding.UTF8.GetBytes(botToken),
                EncryptedSigningSecret = signingSecret is null ? null : Encoding.UTF8.GetBytes(signingSecret),
                MaxRole = maxRole,
                Enabled = true,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateMessagingChannel Create(
        string provider = "telegram", string name = "ops-bot",
        string? botToken = null, string? signingSecret = null, string? appToken = null,
        string? maxRole = null, bool? requireLinkedUser = null, bool? allowUnsigned = null,
        bool? enabled = null, List<string>? allowedExternalIds = null,
        string? externalConfig = null)
        => new()
        {
            Provider = provider,
            Name = name,
            BotToken = botToken,
            SigningSecret = signingSecret,
            AppToken = appToken,
            MaxRole = maxRole,
            RequireLinkedUser = requireLinkedUser,
            AllowUnsigned = allowUnsigned,
            Enabled = enabled,
            AllowedExternalIds = allowedExternalIds,
            ExternalConfig = externalConfig is null ? null : TestJson.Element(externalConfig),
        };

    // Teams is the one provider with a required external_config entry, so the
    // shared helpers below hand it one unless a test is about its absence.
    private const string TeamsConfig = """{"app_id":"app-123"}""";

    private static string? ConfigFor(string provider) =>
        provider.Equals("teams", StringComparison.OrdinalIgnoreCase) ? TeamsConfig : null;

    private static MessagingChannelResponse Ok(ActionResult<MessagingChannelResponse> result)
    {
        Assert.Null(result.Result);
        return Assert.IsType<MessagingChannelResponse>(result.Value);
    }

    // ─── create: validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("discord")]
    [InlineData("irc")]
    [InlineData("")]
    public async Task AnUnknownProviderIsRejected(string provider)
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(provider: provider)));
        Assert.Empty(f.Db.MessagingChannels);
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    [InlineData("whatsapp")]
    [InlineData("teams")]
    [InlineData("Telegram")]
    public async Task EveryKnownProviderIsAcceptedCaseInsensitively(string provider)
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(
            Create(provider: provider, externalConfig: ConfigFor(provider))));

        Assert.Equal(provider.ToLowerInvariant(), body.Provider);
    }

    // app_id is the audience the inbound Bot Framework token is validated
    // against AND the client_id the reply is minted with. A Teams channel saved
    // without one would 401 in both directions with nothing but the delivery
    // log to explain it, so it is refused at the door.
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"tenant_id":"contoso"}""")]
    [InlineData("""{"app_id":""}""")]
    [InlineData("""{"app_id":"   "}""")]
    [InlineData("""{"app_id":123}""")]
    public async Task ATeamsChannelWithoutAnAppIdIsRejected(string? config)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(provider: "teams", externalConfig: config)));

        Assert.Equal("teams_app_id_required", ex.Code);
        Assert.Empty(f.Db.MessagingChannels);
    }

    // The requirement is Teams-specific: the other providers carry their own
    // config (or none) and must not inherit it.
    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    [InlineData("whatsapp")]
    public async Task OtherProvidersDoNotNeedAnAppId(string provider)
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(provider: provider)));

        Assert.Equal(provider, body.Provider);
    }

    // The check runs against the merged channel, so switching an existing
    // channel over to Teams has to bring an app_id with it.
    [Fact]
    public async Task SwitchingAChannelToTeamsWithoutAnAppIdIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(provider: "telegram");

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().UpdateAsync(id, new UpdateMessagingChannel { Provider = "teams" }));
    }

    [Fact]
    public async Task SwitchingAChannelToTeamsWithAnAppIdIsAccepted()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(provider: "telegram");

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateMessagingChannel
        {
            Provider = "teams",
            ExternalConfig = TestJson.Element(TeamsConfig),
        }));

        Assert.Equal("teams", body.Provider);
    }

    // Editing an unrelated field on an already-valid Teams channel must not
    // trip over its own stored config.
    [Fact]
    public async Task EditingATeamsChannelKeepsItsStoredAppId()
    {
        using var f = new Fixture();
        var created = Ok(await f.Build().CreateAsync(
            Create(provider: "teams", externalConfig: TeamsConfig)));

        var body = Ok(await f.Build().UpdateAsync(
            created.MessagingChannelId, new UpdateMessagingChannel { Name = "renamed" }));

        Assert.Equal("renamed", body.Name);
    }

    // Clearing the config on a live Teams channel is the same misconfiguration
    // as never setting it.
    [Fact]
    public async Task ClearingTheConfigOfATeamsChannelIsRejected()
    {
        using var f = new Fixture();
        var created = Ok(await f.Build().CreateAsync(
            Create(provider: "teams", externalConfig: TeamsConfig)));

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().UpdateAsync(created.MessagingChannelId, new UpdateMessagingChannel
            {
                ExternalConfig = TestJson.Element("{}"),
            }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingNameIsRejected(string name)
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() => f.Build().CreateAsync(Create(name: name)));
    }

    // MaxRole is the channel's privilege ceiling; an unknown value stored
    // silently would be ignored at dispatch and the cap would not apply.
    [Theory]
    [InlineData("superadmin")]
    [InlineData("root")]
    public async Task AnUnknownMaxRoleIsRejected(string role)
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Create(maxRole: role)));
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    [InlineData("Operator")]
    public async Task KnownMaxRolesAreNormalised(string role)
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(maxRole: role)));

        Assert.Equal(role.ToLowerInvariant(), body.MaxRole);
    }

    [Fact]
    public async Task NoMaxRoleMeansNoCeiling()
    {
        using var f = new Fixture();

        Assert.Null(Ok(await f.Build().CreateAsync(Create(maxRole: null))).MaxRole);
    }

    // ─── create: defaults + secrets ─────────────────────────────────────

    // The safe posture: linked users required, unsigned deliveries refused.
    [Fact]
    public async Task DefaultsFavourASecureChannel()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create()));

        Assert.True(body.RequireLinkedUser);
        Assert.False(body.AllowUnsigned);
        Assert.True(body.Enabled);
    }

    [Fact]
    public async Task ExplicitFlagsAreHonoured()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(
            requireLinkedUser: false, allowUnsigned: true, enabled: false)));

        Assert.False(body.RequireLinkedUser);
        Assert.True(body.AllowUnsigned);
        Assert.False(body.Enabled);
    }

    [Fact]
    public async Task NameIsTrimmed()
    {
        using var f = new Fixture();

        Assert.Equal("ops-bot", Ok(await f.Build().CreateAsync(Create(name: "  ops-bot  "))).Name);
    }

    // All three secrets are encrypted at rest.
    [Fact]
    public async Task EverySuppliedSecretIsEncrypted()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Create(
            botToken: "bot-123", signingSecret: "sign-456", appToken: "app-789"));

        var row = await f.Db.MessagingChannels.SingleAsync();
        Assert.Equal("bot-123", Encoding.UTF8.GetString(row.EncryptedBotToken!));
        Assert.Equal("sign-456", Encoding.UTF8.GetString(row.EncryptedSigningSecret!));
        Assert.Equal("app-789", Encoding.UTF8.GetString(row.EncryptedAppToken!));
    }

    [Fact]
    public async Task AbsentSecretsStayNull()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Create());

        var row = await f.Db.MessagingChannels.SingleAsync();
        Assert.Null(row.EncryptedBotToken);
        Assert.Null(row.EncryptedSigningSecret);
        Assert.Null(row.EncryptedAppToken);
    }

    // The response tells the admin WHICH secrets are set without revealing any.
    [Fact]
    public async Task TheResponseNeverCarriesASecret()
    {
        using var f = new Fixture();

        var body = Ok(await f.Build().CreateAsync(Create(
            botToken: "bot-123", signingSecret: "sign-456", appToken: "app-789")));

        // Walk every string-valued property rather than serialising: the DTO
        // carries an uninitialised JsonElement (ExternalConfig) that the
        // serialiser refuses, and this checks the same thing more directly.
        var leaked = body.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (string?)p.GetValue(body))
            .Where(v => v is not null
                        && (v.Contains("bot-123") || v.Contains("sign-456") || v.Contains("app-789")))
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public async Task AllowedExternalIdsDefaultToAnEmptyList()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Create());

        Assert.Empty((await f.Db.MessagingChannels.SingleAsync()).AllowedExternalIds);
    }

    [Fact]
    public async Task AllowedExternalIdsAreStored()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Create(allowedExternalIds: new List<string> { "ext-1" }));

        Assert.Equal("ext-1",
            Assert.Single((await f.Db.MessagingChannels.SingleAsync()).AllowedExternalIds));
    }

    // ─── update: the three-state secret rule ────────────────────────────

    [Fact]
    public async Task ANullSecretLeavesItUntouched()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(botToken: "original");

        await f.Build().UpdateAsync(id, new UpdateMessagingChannel { BotToken = null });

        Assert.Equal("original",
            Encoding.UTF8.GetString((await f.Db.MessagingChannels.SingleAsync()).EncryptedBotToken!));
    }

    [Fact]
    public async Task AnEmptySecretClearsIt()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(botToken: "original");

        await f.Build().UpdateAsync(id, new UpdateMessagingChannel { BotToken = "" });

        Assert.Null((await f.Db.MessagingChannels.SingleAsync()).EncryptedBotToken);
    }

    [Fact]
    public async Task ANewSecretRotatesIt()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(botToken: "original");

        await f.Build().UpdateAsync(id, new UpdateMessagingChannel { BotToken = "rotated" });

        Assert.Equal("rotated",
            Encoding.UTF8.GetString((await f.Db.MessagingChannels.SingleAsync()).EncryptedBotToken!));
    }

    // The same rule holds for the signing secret — clearing it is what turns
    // signature verification off, so it must be deliberate.
    [Fact]
    public async Task TheSigningSecretFollowsTheSameThreeStateRule()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(signingSecret: "original");
        var svc = f.Build();

        await svc.UpdateAsync(id, new UpdateMessagingChannel { SigningSecret = null });
        Assert.NotNull((await f.Db.MessagingChannels.SingleAsync()).EncryptedSigningSecret);

        await svc.UpdateAsync(id, new UpdateMessagingChannel { SigningSecret = "" });
        Assert.Null((await f.Db.MessagingChannels.SingleAsync()).EncryptedSigningSecret);
    }

    // ─── update: partial semantics ──────────────────────────────────────

    [Fact]
    public async Task AnEmptyPatchChangesNothing()
    {
        using var f = new Fixture();
        var id = f.SeedChannel(name: "original", maxRole: "viewer");

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateMessagingChannel()));

        Assert.Equal("original", body.Name);
        Assert.Equal("viewer", body.MaxRole);
        Assert.True(body.Enabled);
    }

    [Fact]
    public async Task SuppliedFieldsAreApplied()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        var body = Ok(await f.Build().UpdateAsync(id, new UpdateMessagingChannel
        {
            Name = "  renamed  ",
            Provider = "Slack",
            MaxRole = "Operator",
            RequireLinkedUser = false,
            AllowUnsigned = true,
            Enabled = false,
        }));

        Assert.Equal("renamed", body.Name);
        Assert.Equal("slack", body.Provider);
        Assert.Equal("operator", body.MaxRole);
        Assert.False(body.RequireLinkedUser);
        Assert.True(body.AllowUnsigned);
        Assert.False(body.Enabled);
    }

    // A blank provider or name is "not supplied", not "clear it".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankProviderOrNameIsIgnored(string blank)
    {
        using var f = new Fixture();
        var id = f.SeedChannel(provider: "telegram", name: "original");

        var body = Ok(await f.Build().UpdateAsync(
            id, new UpdateMessagingChannel { Provider = blank, Name = blank }));

        Assert.Equal("telegram", body.Provider);
        Assert.Equal("original", body.Name);
    }

    [Fact]
    public async Task AnInvalidProviderOnUpdateIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().UpdateAsync(id, new UpdateMessagingChannel { Provider = "discord" }));
    }

    [Fact]
    public async Task AnInvalidMaxRoleOnUpdateIsRejected()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().UpdateAsync(id, new UpdateMessagingChannel { MaxRole = "root" }));
    }

    [Fact]
    public async Task UpdatingAnUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().UpdateAsync(Guid.NewGuid(), new UpdateMessagingChannel()));
    }

    // ─── delete ─────────────────────────────────────────────────────────

    // Deleting also disables: a soft-deleted row must not keep accepting
    // webhook deliveries.
    [Fact]
    public async Task DeleteSoftDeletesAndDisables()
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        var result = await f.Build().DeleteAsync(id);

        Assert.IsType<NoContentResult>(result);
        var row = await f.Db.MessagingChannels.SingleAsync();
        Assert.False(row.IsActive);
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task DeletingAnUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().DeleteAsync(Guid.NewGuid()));
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListReturnsChannels()
    {
        using var f = new Fixture();
        f.SeedChannel(name: "a");
        f.SeedChannel(name: "b");

        var ok = Assert.IsType<OkObjectResult>((await f.Build().ListAsync(50, 0)).Result);
        var body = Assert.IsType<ListResponse<MessagingChannelResponse>>(ok.Value);

        Assert.Equal(2, body.Total);
    }

    [Fact]
    public async Task GetUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Build().GetAsync(Guid.NewGuid()));
    }

    // The activity view is capped so a busy channel can't be asked for its
    // whole history in one call.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9999)]
    [InlineData(50)]
    public async Task ActivityLimitIsClamped(int requested)
    {
        using var f = new Fixture();
        var id = f.SeedChannel();

        var result = await f.Build().GetActivityAsync(id, requested);

        Assert.Null(result.Result);   // clamped, not rejected
    }

    [Fact]
    public async Task ActivityForAnUnknownChannelIsNotFound()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().GetActivityAsync(Guid.NewGuid(), 50));
    }
}
