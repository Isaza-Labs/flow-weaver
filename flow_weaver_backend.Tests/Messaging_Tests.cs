using System.Net;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Messaging.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

public class MessagingChannelServiceTests
{
    private readonly FakeUser _caller = new();

    private MessagingChannelService NewSvc(AppDbContext db)
        => new(new MessagingChannelRepository(db), new MessagingDeliveryRepository(db),
               new MessagingIdentityLinkRepository(db), new FakeCrypto(), _caller,
               Options.Create(new MessagingOptions()), new FakeAudit());

    private static CreateMessagingChannel Sample(string name = "ops-bot")
        => new() { Provider = MessagingChannel.ProviderTelegram, Name = name, BotToken = "123456:token" };

    [Fact]
    public async Task Create_persists_channel()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db).CreateAsync(Sample());

        Assert.IsNotType<BadRequestObjectResult>(result.Result);
        Assert.Single(db.Set<MessagingChannel>());
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ValidationException>(() =>
            NewSvc(db).CreateAsync(new CreateMessagingChannel { Provider = MessagingChannel.ProviderSlack, Name = "" }));
    }

    [Fact]
    public async Task Create_rejects_unknown_provider()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<ValidationException>(() =>
            NewSvc(db).CreateAsync(new CreateMessagingChannel { Provider = "carrier-pigeon", Name = "x" }));
    }

    [Fact]
    public async Task Get_missing_throws_not_found()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(() => NewSvc(db).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task List_and_delete()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db);
        await svc.CreateAsync(Sample());
        var id = db.Set<MessagingChannel>().Single().MessagingChannelId;

        var ok = Assert.IsType<OkObjectResult>((await svc.ListAsync(50, 0)).Result);
        Assert.Equal(1, Assert.IsType<ListResponse<MessagingChannelResponse>>(ok.Value).Total);

        await svc.DeleteAsync(id);
        Assert.False(db.Set<MessagingChannel>().Single().IsActive);
    }

    [Fact]
    public async Task Update_missing_throws_not_found()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(() => NewSvc(db).UpdateAsync(Guid.NewGuid(), new UpdateMessagingChannel()));
    }
}

public class MessagingProviderResolverTests
{
    [Fact]
    public void Resolve_returns_provider_by_name_or_null()
    {
        var factory = new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}"));
        var resolver = new MessagingProviderResolver(new IMessagingProvider[]
        {
            new TelegramProvider(factory, NullLogger<TelegramProvider>.Instance),
            new SlackProvider(factory, NullLogger<SlackProvider>.Instance),
            new WhatsAppProvider(factory, NullLogger<WhatsAppProvider>.Instance),
            new TeamsProvider(factory, NullLogger<TeamsProvider>.Instance),
        });

        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderTelegram));
        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderSlack));
        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderTeams));
        Assert.Null(resolver.Resolve("carrier-pigeon"));
    }

    // TeamsProvider has a second constructor that takes the OpenID metadata
    // source so the verification path is testable. Only the two-argument one is
    // satisfiable from the container, and picking wrong is a startup crash
    // rather than anything a unit test of the provider itself would catch.
    [Fact]
    public void Every_provider_is_constructible_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")));
        services.AddSingleton<IMessagingProvider, TelegramProvider>();
        services.AddSingleton<IMessagingProvider, SlackProvider>();
        services.AddSingleton<IMessagingProvider, WhatsAppProvider>();
        services.AddSingleton<IMessagingProvider, TeamsProvider>();
        services.AddSingleton<IMessagingProviderResolver, MessagingProviderResolver>();

        using var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IMessagingProviderResolver>();

        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderTeams));
        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderSlack));
        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderTelegram));
        Assert.NotNull(resolver.Resolve(MessagingChannel.ProviderWhatsApp));
    }
}

public class MessagingLinkServiceTests
{
    private readonly FakeUser _caller = new();

    private MessagingLinkService NewSvc(AppDbContext db)
        => new(new MessagingLinkTokenRepository(db), new MessagingChannelRepository(db),
               new MessagingIdentityLinkRepository(db), _caller, Options.Create(new MessagingOptions()));

    [Fact]
    public async Task Preview_unknown_token_throws_not_found()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(() => NewSvc(db).PreviewAsync("bogus-token"));
    }

    [Fact]
    public async Task Confirm_unknown_token_throws_not_found()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(() => NewSvc(db).ConfirmAsync("bogus-token"));
    }
}
