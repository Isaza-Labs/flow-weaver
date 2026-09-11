using System.Net;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Net;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// ISnippetHandler.ExecuteAsync(SnippetRequest) -> SnippetResult. These cover the
// handlers reachable without live infra: the not-implemented stubs, and the
// validation/lookup error paths of the network handlers (no real device/HTTP hit).
public class WorkerHandlerTests
{
    private static SnippetRequest Req(object? input = null, Guid? deviceId = null, string type = "test")
        => new()
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "n1",
            SnippetId = Guid.NewGuid(),
            SnippetType = type,
            InputPayload = JsonSerializer.SerializeToElement(input ?? new { }),
            DeviceId = deviceId,
        };

    private static IConfiguration EmptyConfig => new ConfigurationBuilder().Build();

    [Fact]
    public async Task Snmp_stub_returns_not_implemented()
    {
        var h = new SnmpV3Handler(NullLogger<SnmpV3Handler>.Instance);
        Assert.Equal("snmp_v3", h.Type);
        var result = await h.ExecuteAsync(Req(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("not implemented", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Netconf_stub_returns_not_implemented()
    {
        var h = new NetconfHandler(NullLogger<NetconfHandler>.Instance);
        Assert.Equal("netconf", h.Type);
        var result = await h.ExecuteAsync(Req(), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Ping_missing_device_returns_error()
    {
        using var db = TestDb.NewContext();
        var h = new PingHandler(new DeviceRepository(db), NullLogger<PingHandler>.Instance);
        var result = await h.ExecuteAsync(Req(deviceId: Guid.NewGuid(), type: "ping"), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task RestCall_missing_url_returns_error()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var h = new RestCallHandler(new FakeHttpClientFactory(handler),
            new UrlGuard(EmptyConfig, NullLogger<UrlGuard>.Instance), new PassThroughSecrets(),
            NullLogger<RestCallHandler>.Instance);
        var result = await h.ExecuteAsync(Req(new { }, type: "rest_call"), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Slack_missing_token_returns_error()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{\"ok\":true}");
        var h = new SlackHandler(new FakeHttpClientFactory(handler), EmptyConfig, NullLogger<SlackHandler>.Instance);
        var result = await h.ExecuteAsync(Req(new { channel = "#ops", text = "hi" }, type: "slack_message"), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Slack_with_token_posts_and_succeeds()
    {
        // Slack talks to a fixed api URL (not user-controlled) so it skips the SSRF
        // guard — the fake handler can stand in for chat.postMessage directly.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"1720000000.1\"}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Slack:BotToken"] = "xoxb-test-token" })
            .Build();
        var h = new SlackHandler(new FakeHttpClientFactory(handler), config, NullLogger<SlackHandler>.Instance);

        var result = await h.ExecuteAsync(Req(new { channel = "#ops", text = "deploy done" }, type: "slack_message"), CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task IntegrationAction_missing_integration_returns_error()
    {
        using var db = TestDb.NewContext();
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var h = new IntegrationActionHandler(
            new IntegrationRepository(db), new IntegrationActionRepository(db),
            new FakeHttpClientFactory(handler), TestAuth.Applier(),
            new UrlGuard(EmptyConfig, NullLogger<UrlGuard>.Instance), NullLogger<IntegrationActionHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req(new { integration_id = Guid.NewGuid(), action_id = Guid.NewGuid() }, type: "integration_action"),
            CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Ssh_missing_host_returns_error_before_spawn()
    {
        using var db = TestDb.NewContext();
        var h = new SshHandler(
            new DeviceRepository(db), new RepositoryBase<CredentialModel>(db), new WorkflowRunRepository(db),
            new FakeCrypto(), new FakePolicyEvaluator(), new FakeVendorCommandValidator(),
            new PassThroughSecrets(), EmptyConfig, NullLogger<SshHandler>.Instance);

        // No host/command → the handler rejects before ever spawning the runner.
        var result = await h.ExecuteAsync(Req(new { }, type: "ssh"), CancellationToken.None);
        Assert.False(result.Success);
    }
}
