using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.AiApiSpec;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// The OpenAPI catalogue and the chat-identity links on a messaging
// channel.
//
// The spec catalogue backs every `discover_operations` / `execute_operation`
// call, so a spec that saves but doesn't index leaves the agent unable to see
// operations it was just told about. The identity links are the mapping from
// an external chat account to a FlowWeaver user — revoking one has to stop
// that account acting as that user immediately.
public class ApiSpecAndChannelLinkTests
{

    // ─── AiApiSpecService ───────────────────────────────────────────────

    private sealed class RecordingIndex : IApiSpecIndex
    {
        public int Reloads { get; private set; }
        public Task ReloadAsync(CancellationToken ct = default)
        {
            Reloads++;
            return Task.CompletedTask;
        }
        public IReadOnlyList<ApiOperation> All() => Array.Empty<ApiOperation>();
        public IReadOnlyList<ApiOperation> Search(
            string keyword, string? api = null, string? method = null)
            => Array.Empty<ApiOperation>();
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private sealed class SpecFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingIndex Index { get; } = new();

        public AiApiSpecService Build() => new(
            new AiApiSpecRepository(Db),
            new FakeUser(),
            Index,
            NullLogger<AiApiSpecService>.Instance);

        public Guid Seed(string api = "netbox", string? content = null)
        {
            var id = Guid.NewGuid();
            Db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = id,
                Api = api,
                Content = content ?? ValidSpec,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private const string ValidSpec = """
        openapi: 3.0.0
        info:
          title: netbox
          version: "1.0"
        paths:
          /dcim/devices/:
            get:
              summary: list devices
        """;

    private static string ErrorOf<T>(ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    [Fact]
    public async Task Spec_UpdatingAnUnknownIdIs404()
    {
        using var f = new SpecFixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateAiApiSpec { Api = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // The api name becomes the tool prefix (`netbox:listDevices`), so it has
    // to stay identifier-safe.
    [Theory]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("")]
    public async Task Spec_AnInvalidApiNameIsRejected(string api)
    {
        using var f = new SpecFixture();
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Api = api });

        Assert.Contains("api must match", ErrorOf(result));
        Assert.Equal("netbox", f.Db.AiApiSpecs.Single().Api);
    }

    [Fact]
    public async Task Spec_TheApiNameIsLowercasedAndTrimmed()
    {
        using var f = new SpecFixture();
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Api = "  NetBox2  " });

        Assert.Equal("netbox2", f.Db.AiApiSpecs.Single().Api);
    }

    // Two specs sharing an api name would make `api:operationId` ambiguous.
    [Fact]
    public async Task Spec_RenamingOntoAnotherSpecsApiIs409()
    {
        using var f = new SpecFixture();
        f.Seed("netbox");
        var id = f.Seed("nautobot");

        var result = await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Api = "netbox" });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Spec_RenamingToItsOwnNameIsAccepted()
    {
        using var f = new SpecFixture();
        var id = f.Seed("netbox");

        var result = await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Api = "netbox" });

        Assert.Null(result.Result);
    }

    // Storing YAML the parser can't read would leave the agent with a spec it
    // can never slice; it is rejected at save time with the parser's reason.
    [Fact]
    public async Task Spec_InvalidYamlIsRejectedWithTheParserReason()
    {
        using var f = new SpecFixture();
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(
            id, new UpdateAiApiSpec { Content = "paths:\n  - broken: [unclosed" });

        Assert.Contains("invalid YAML", ErrorOf(result));
        Assert.Equal(ValidSpec, f.Db.AiApiSpecs.Single().Content);
    }

    [Fact]
    public async Task Spec_ValidContentIsPersisted()
    {
        using var f = new SpecFixture();
        var id = f.Seed();
        var replacement = ValidSpec.Replace("list devices", "list all devices");

        await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Content = replacement });

        Assert.Equal(replacement, f.Db.AiApiSpecs.Single().Content);
    }

    // The in-memory index backs every discover/execute call — a spec that
    // saves without re-indexing is invisible to the agent until restart.
    [Fact]
    public async Task Spec_AnUpdateReindexesTheCatalogue()
    {
        using var f = new SpecFixture();
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateAiApiSpec { Content = ValidSpec });

        Assert.Equal(1, f.Index.Reloads);
    }

    [Fact]
    public async Task Spec_OmittedFieldsAreLeftUntouched()
    {
        using var f = new SpecFixture();
        var id = f.Seed("netbox");

        await f.Build().UpdateAsync(id, new UpdateAiApiSpec { IsActive = true });

        var saved = f.Db.AiApiSpecs.Single();
        Assert.Equal("netbox", saved.Api);
        Assert.Equal(ValidSpec, saved.Content);
    }

    // ─── MessagingChannelService identity links ─────────────────────────

    private sealed class ChannelFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public MessagingChannelService Build() => new(
            new MessagingChannelRepository(Db),
            new MessagingDeliveryRepository(Db),
            new MessagingIdentityLinkRepository(Db),
            new FakeCrypto(),
            new FakeUser(),
            new OptionsWrapper<MessagingOptions>(new MessagingOptions()),
            new FakeAudit());

        public Guid SeedChannel()
        {
            var id = Guid.NewGuid();
            Db.MessagingChannels.Add(new MessagingChannel
            {
                MessagingChannelId = id,
                Name = "ops",
                Provider = MessagingChannel.ProviderTelegram,
                Enabled = true,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedLink(
            Guid channelId, string externalUserId = "U9",
            bool active = true)
        {
            var id = Guid.NewGuid();
            Db.MessagingIdentityLinks.Add(new MessagingIdentityLink
            {
                MessagingIdentityLinkId = id,
                MessagingChannelId = channelId,
                ExternalWorkspaceId = "W1",
                ExternalUserId = externalUserId,
                LinkedUserId = Guid.NewGuid(),
                DisplayName = "alice",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class OptionsWrapper<T> : IOptions<T> where T : class
    {
        public OptionsWrapper(T value) => Value = value;
        public T Value { get; }
    }

    private static List<MessagingIdentityLinkResponse> Links(
        ActionResult<ListResponse<MessagingIdentityLinkResponse>> result)
        => Assert.IsType<ListResponse<MessagingIdentityLinkResponse>>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Data.ToList();

    [Fact]
    public async Task Links_ListsTheChannelsLinkedIdentities()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();
        f.SeedLink(channelId, "U9");

        var links = Links(await f.Build().ListLinksAsync(channelId));

        var link = Assert.Single(links);
        Assert.Equal("U9", link.ExternalUserId);
        Assert.Equal("W1", link.ExternalWorkspaceId);
        Assert.Equal("alice", link.DisplayName);
        Assert.NotEqual(Guid.Empty, link.LinkedUserId);
    }

    [Fact]
    public async Task Links_AnUnknownChannelIsNotFound()
    {
        using var f = new ChannelFixture();

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().ListLinksAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Links_OnlyTheAskedChannelsLinksAreListed()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();
        var other = f.SeedChannel();
        f.SeedLink(channelId, "mine");
        f.SeedLink(other, "theirs");

        Assert.Equal("mine", Assert.Single(Links(await f.Build().ListLinksAsync(channelId))).ExternalUserId);
    }

    [Fact]
    public async Task Links_RevokedLinksAreNoLongerListed()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();
        f.SeedLink(channelId, active: false);

        Assert.Empty(Links(await f.Build().ListLinksAsync(channelId)));
    }

    // Revoking is what stops an external chat account acting as a FlowWeaver
    // user; it must take effect on the very next listing.
    [Fact]
    public async Task Links_RevokeDeactivatesTheLink()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();
        var linkId = f.SeedLink(channelId);

        var result = await f.Build().RevokeLinkAsync(channelId, linkId);

        Assert.IsType<NoContentResult>(result);
        Assert.False(f.Db.MessagingIdentityLinks.Single().IsActive);
        Assert.Empty(Links(await f.Build().ListLinksAsync(channelId)));
    }

    [Fact]
    public async Task Links_RevokingAnUnknownLinkIsNotFound()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().RevokeLinkAsync(channelId, Guid.NewGuid()));
    }

    // The link must belong to the channel in the route — otherwise a caller
    // could revoke a link on a channel they weren't looking at.
    [Fact]
    public async Task Links_RevokingALinkFromAnotherChannelIsNotFound()
    {
        using var f = new ChannelFixture();
        var channelId = f.SeedChannel();
        var other = f.SeedChannel();
        var linkId = f.SeedLink(other);

        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Build().RevokeLinkAsync(channelId, linkId));

        Assert.True(f.Db.MessagingIdentityLinks.Single().IsActive);
    }

}
