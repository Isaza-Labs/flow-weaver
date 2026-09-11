using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Tests;

// MessagingIngestService is the unauthenticated entry point for every inbound
// chat message. Its job is to refuse first and enqueue last: unknown channel,
// bad signature, unlisted external id, unlinked identity and a full queue must
// all stop before an agent job exists. The no-escalation rule lives here — an
// unverified external identity never gets the agent to run.
public class MessagingIngestServiceTests
{
    private const string Provider = "telegram";

    // Scriptable provider double: each test decides how verification and
    // parsing resolve, so every ingest branch is reachable without crafting
    // real Telegram/Slack payloads.
    private sealed class StubProvider : IMessagingProvider
    {
        public string Provider { get; init; } = MessagingIngestServiceTests.Provider;
        public WebhookVerifyResult VerifyResult { get; set; } = WebhookVerifyResult.Verified();
        public InboundMessage? Inbound { get; set; } = new()
        {
            ProviderEventId = "evt-1",
            ExternalUserId = "ext-user",
            ExternalThreadId = "thread-1",
            Text = "hello",
        };
        public int VerifyCalls { get; private set; }
        public string? SecretSeen { get; private set; }
        public List<OutboundMessage> Sent { get; } = new();

        public Task<WebhookVerifyResult> VerifyAsync(
            MessagingChannel channel, MessagingHttpRequest request,
            string? decryptedSigningSecret, CancellationToken ct)
        {
            VerifyCalls++;
            SecretSeen = decryptedSigningSecret;
            return Task.FromResult(VerifyResult);
        }

        public InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request) => Inbound;

        public Task SendAsync(MessagingChannel channel, string? decryptedBotToken,
            OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class StubLinkService : IMessagingLinkService
    {
        public LinkInvite Invite { get; set; } = new("cleartext", "https://app.example.com/link?token=abc");
        public int Created { get; private set; }

        public Task<LinkInvite> CreateLinkAsync(
            MessagingChannel channel, string? externalWorkspaceId, string externalUserId, CancellationToken ct)
        {
            Created++;
            return Task.FromResult(Invite);
        }

        public Task<Microsoft.AspNetCore.Mvc.ActionResult<MessagingLinkPreviewResponse>> PreviewAsync(string token)
            => throw new NotSupportedException();
        public Task<Microsoft.AspNetCore.Mvc.ActionResult<MessagingLinkConfirmResponse>> ConfirmAsync(string token)
            => throw new NotSupportedException();
    }

    // Records what got enqueued so the "at most once" and "link prompt" paths
    // are observable.
    private sealed class RecordingQueue : IQueueRepository
    {
        public List<(string Type, JsonElement Payload)> Enqueued { get; } = new();

        public Task<Guid> EnqueueAsync(string type, JsonElement payload, string tag, int priority, CancellationToken ct)
        {
            Enqueued.Add((type, payload));
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<Job?> ClaimAsync(string[] tags, string workerId, CancellationToken ct) => Task.FromResult<Job?>(null);
        public Task CompleteAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(Guid jobId, string error, CancellationToken ct) => Task.CompletedTask;
        public Task<int> ReclaimExpiredAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct) => Task.FromResult(true);
        public Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class Fixture : IDisposable
    {
        public StubProvider Provider { get; } = new();
        public StubLinkService Links { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public ServiceProvider Sp { get; }
        private readonly string _dbName = Guid.NewGuid().ToString();

        public Fixture(MessagingOptions? options = null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<IMessagingChannelRepository, MessagingChannelRepository>();
            services.AddScoped<IMessagingDeliveryRepository, MessagingDeliveryRepository>();
            services.AddScoped<IMessagingIdentityLinkRepository, MessagingIdentityLinkRepository>();
            services.AddScoped<IAIConversationRepository, AIConversationRepository>();
            services.AddScoped<IJobRepository, JobRepository>();
            services.AddSingleton<IQueueRepository>(Queue);
            services.AddSingleton<ICredentialEncryptionService, FakeCrypto>();
            services.AddSingleton<IMessagingProvider>(Provider);
            services.AddSingleton<IMessagingProviderResolver, MessagingProviderResolver>();
            services.AddSingleton<IMessagingLinkService>(Links);
            services.AddHttpContextAccessor();
            services.AddScoped<MutableCurrentUser>();
            services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<MutableCurrentUser>());
            services.AddSingleton(Options.Create(options ?? new MessagingOptions()));
            services.AddLogging();
            Sp = services.BuildServiceProvider();
        }

        public MessagingIngestService Build() => new(
            Sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MessagingIngestService>.Instance);

        public AppDbContext NewDb()
        {
            var scope = Sp.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDbContext>();
        }

        public Guid SeedChannel(
            string provider = MessagingIngestServiceTests.Provider,
            bool enabled = true,
            bool requireLinkedUser = true,
            List<string>? allowedExternalIds = null,
            string? signingSecret = "s3cret",
            Guid? agentId = null)
        {
            using var scope = Sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = Guid.NewGuid();
            db.MessagingChannels.Add(new MessagingChannel
            {
                MessagingChannelId = id,
                Provider = provider,
                Name = "chan",
                Enabled = enabled,
                RequireLinkedUser = requireLinkedUser,
                AllowedExternalIds = allowedExternalIds ?? new List<string>(),
                EncryptedSigningSecret = signingSecret is null ? null : Encoding.UTF8.GetBytes(signingSecret),
                DefaultAgentId = agentId,
                IsActive = true,
            });
            db.SaveChanges();
            return id;
        }

        public Guid SeedIdentityLink(Guid channelId, string externalUserId = "ext-user", string workspace = "")
        {
            using var scope = Sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = Guid.NewGuid();
            db.MessagingIdentityLinks.Add(new MessagingIdentityLink
            {
                MessagingIdentityLinkId = Guid.NewGuid(),
                MessagingChannelId = channelId,
                ExternalWorkspaceId = workspace,
                ExternalUserId = externalUserId,
                LinkedUserId = userId,
                IsActive = true,
            });
            db.SaveChanges();
            return userId;
        }

        public void SeedJobs(int count, string status = JobStatus.Pending)
        {
            using var scope = Sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < count; i++)
            {
                db.Jobs.Add(new Job
                {
                    JobId = Guid.NewGuid(),
                    Type = "agent_message",
                    Status = status,
                    Tag = "messaging",
                    IsActive = true,
                });
            }
            db.SaveChanges();
        }

        public void Dispose() => Sp.Dispose();
    }

    private static MessagingHttpRequest Request(string body = """{"any":"payload"}""")
        => new() { Method = "POST", Body = Encoding.UTF8.GetBytes(body) };

    // ─── channel resolution ─────────────────────────────────────────────

    [Fact]
    public async Task UnknownChannel_Is404()
    {
        using var f = new Fixture();

        var outcome = await f.Build().ReceiveAsync(Provider, Guid.NewGuid(), Request(), default);

        Assert.Equal(404, outcome.StatusCode);
        Assert.Empty(f.Queue.Enqueued);
    }

    // The provider in the URL must match the channel row, otherwise a webhook
    // for one platform could be replayed against another's channel.
    [Fact]
    public async Task ProviderMismatch_Is404()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();

        var outcome = await f.Build().ReceiveAsync("slack", channelId, Request(), default);

        Assert.Equal(404, outcome.StatusCode);
    }

    [Fact]
    public async Task UnsupportedProvider_Is501()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(provider: "discord");

        var outcome = await f.Build().ReceiveAsync("discord", channelId, Request(), default);

        Assert.Equal(501, outcome.StatusCode);
    }

    [Fact]
    public async Task DisabledChannel_Is403()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(enabled: false);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(403, outcome.StatusCode);
        Assert.Empty(f.Queue.Enqueued);
    }

    // ─── signature verification ─────────────────────────────────────────

    [Fact]
    public async Task RejectedSignature_UsesTheProvidersStatusAndReason()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.VerifyResult = WebhookVerifyResult.Rejected(401, "bad signature");

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Equal("bad signature", outcome.Body);
        Assert.Empty(f.Queue.Enqueued);
    }

    // A provider handshake (Slack url_verification) must echo the body verbatim.
    [Fact]
    public async Task ChallengeIsEchoedVerbatim()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.VerifyResult = WebhookVerifyResult.Challenge("token-abc", "application/json");

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("token-abc", outcome.Body);
        Assert.Equal("application/json", outcome.ContentType);
        Assert.True(outcome.Raw);
    }

    // The signing secret reaches the provider decrypted — providers never touch
    // the keyring themselves.
    [Fact]
    public async Task SigningSecretIsDecryptedBeforeVerification()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(signingSecret: "top-secret");
        f.SeedIdentityLink(channelId);

        await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal("top-secret", f.Provider.SecretSeen);
    }

    // Socket Mode already authenticated the transport, so verification is skipped.
    [Fact]
    public async Task ReceiveVerified_SkipsSignatureVerification()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        f.Provider.VerifyResult = WebhookVerifyResult.Rejected(401, "would have failed");

        var outcome = await f.Build().ReceiveVerifiedAsync(
            Provider, channelId, Encoding.UTF8.GetBytes("{}"), default);

        Assert.Equal(0, f.Provider.VerifyCalls);
        Assert.Equal(202, outcome.StatusCode);
    }

    // ─── payload parsing / dedupe ───────────────────────────────────────

    // Delivery receipts, bot echoes and non-message events parse to null and
    // must be acknowledged without work.
    [Fact]
    public async Task NonActionablePayload_Is200WithoutEnqueueing()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.Inbound = null;

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Empty(f.Queue.Enqueued);
    }

    // Providers re-deliver on timeout; the same event id must not run the agent
    // twice.
    [Fact]
    public async Task DuplicateEventId_IsIgnored()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        var svc = f.Build();
        await svc.ReceiveAsync(Provider, channelId, Request(), default);

        var second = await svc.ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(200, second.StatusCode);
        Assert.Equal("duplicate", second.Body);
        Assert.Single(f.Queue.Enqueued);   // only the first turn was queued
    }

    // ─── allowlist ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExternalIdNotInAllowlist_IsRejectedAndAudited()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(allowedExternalIds: new List<string> { "someone-else" });
        f.SeedIdentityLink(channelId);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Empty(f.Queue.Enqueued);

        using var db = f.NewDb();
        var evt = await db.MessagingInboundEvents.SingleAsync();
        Assert.Equal(MessagingInboundEvent.StatusRejected, evt.Status);
        Assert.Contains("allowlist", evt.Error);
    }

    [Fact]
    public async Task ExternalIdInAllowlist_IsAccepted()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(allowedExternalIds: new List<string> { "ext-user" });
        f.SeedIdentityLink(channelId);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Single(f.Queue.Enqueued);
    }

    // An empty allowlist means "no restriction", not "deny everything".
    [Fact]
    public async Task EmptyAllowlistDoesNotBlock()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(allowedExternalIds: new List<string>());
        f.SeedIdentityLink(channelId);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
    }

    // ─── identity linking (the no-escalation rule) ──────────────────────

    // An unverified external identity must never get the agent to run; instead
    // it receives a self-service linking prompt.
    [Fact]
    public async Task UnlinkedIdentity_SendsLinkPromptAndDoesNotRunTheAgent()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(requireLinkedUser: true);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(1, f.Links.Created);

        // Exactly one job, and it is a plain send — never an agent turn.
        var job = Assert.Single(f.Queue.Enqueued);
        Assert.Equal(MessagingJobTypes.Send, job.Type);
        Assert.Contains("https://app.example.com/link?token=abc",
            job.Payload.GetProperty("Text").GetString());
    }

    // Without a public base URL we still prompt, just without a clickable link.
    [Fact]
    public async Task UnlinkedIdentity_WithoutPublicBaseUrl_StillPrompts()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(requireLinkedUser: true);
        f.Links.Invite = new LinkInvite("cleartext", string.Empty);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        var job = Assert.Single(f.Queue.Enqueued);
        Assert.Equal(MessagingJobTypes.Send, job.Type);
        Assert.DoesNotContain("http", job.Payload.GetProperty("Text").GetString()!);
    }

    // With linking not required, an unknown identity is simply refused — it
    // still must not reach the agent.
    [Fact]
    public async Task UnlinkedIdentity_WhenLinkingNotRequired_IsRejectedOutright()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(requireLinkedUser: false);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal("no linked user", outcome.Body);
        Assert.Empty(f.Queue.Enqueued);
        Assert.Equal(0, f.Links.Created);
    }

    // ─── conversation resolution ────────────────────────────────────────

    [Fact]
    public async Task FirstMessageCreatesAConversationBoundToTheThread()
    {
        using var f = new Fixture();
        var agentId = Guid.NewGuid();
        var channelId = f.SeedChannel(agentId: agentId);
        var userId = f.SeedIdentityLink(channelId);

        await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        using var db = f.NewDb();
        var convo = await db.AIConversations.SingleAsync();
        Assert.Equal(userId.ToString(), convo.UserId);
        Assert.Equal(channelId, convo.MessagingChannelId);
        Assert.Equal("thread-1", convo.ExternalThreadId);
        Assert.Equal(Provider, convo.Source);
        Assert.Equal(agentId, convo.AgentId);
        Assert.Equal("active", convo.Status);
    }

    // A second message in the same thread must continue the conversation, not
    // fork a new one — otherwise the agent loses all context.
    [Fact]
    public async Task SecondMessageInSameThreadReusesTheConversation()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        var svc = f.Build();
        await svc.ReceiveAsync(Provider, channelId, Request(), default);

        f.Provider.Inbound = new InboundMessage
        {
            ProviderEventId = "evt-2",
            ExternalUserId = "ext-user",
            ExternalThreadId = "thread-1",
            Text = "follow up",
        };
        await svc.ReceiveAsync(Provider, channelId, Request(), default);

        using var db = f.NewDb();
        Assert.Single(await db.AIConversations.ToListAsync());
        Assert.Equal(2, f.Queue.Enqueued.Count);
        var first = f.Queue.Enqueued[0].Payload.GetProperty("ConversationId").GetGuid();
        var second = f.Queue.Enqueued[1].Payload.GetProperty("ConversationId").GetGuid();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DifferentThreadsGetSeparateConversations()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        var svc = f.Build();
        await svc.ReceiveAsync(Provider, channelId, Request(), default);

        f.Provider.Inbound = new InboundMessage
        {
            ProviderEventId = "evt-2",
            ExternalUserId = "ext-user",
            ExternalThreadId = "thread-2",
            Text = "other thread",
        };
        await svc.ReceiveAsync(Provider, channelId, Request(), default);

        using var db = f.NewDb();
        Assert.Equal(2, await db.AIConversations.CountAsync());
    }

    // ─── backpressure ───────────────────────────────────────────────────

    // Shedding load beats unbounded queue growth; the provider will retry.
    [Fact]
    public async Task QueueAtCapacity_Returns503AndDoesNotEnqueue()
    {
        using var f = new Fixture(new MessagingOptions { MaxQueueDepth = 2 });
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        f.SeedJobs(2);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Empty(f.Queue.Enqueued);

        using var db = f.NewDb();
        var evt = await db.MessagingInboundEvents.SingleAsync();
        Assert.Equal(MessagingInboundEvent.StatusRejected, evt.Status);
        Assert.Contains("queue full", evt.Error);
    }

    [Fact]
    public async Task BelowCapacity_IsAccepted()
    {
        using var f = new Fixture(new MessagingOptions { MaxQueueDepth = 5 });
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);
        f.SeedJobs(2);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Single(f.Queue.Enqueued);
    }

    // ─── the queued happy path ──────────────────────────────────────────

    [Fact]
    public async Task QueuesAnAgentMessageJobWithTheResolvedIdentity()
    {
        using var f = new Fixture();
        var agentId = Guid.NewGuid();
        var channelId = f.SeedChannel(agentId: agentId);
        var userId = f.SeedIdentityLink(channelId);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal("queued", outcome.Body);

        var job = Assert.Single(f.Queue.Enqueued);
        Assert.Equal(MessagingJobTypes.AgentMessage, job.Type);
        Assert.Equal(channelId, job.Payload.GetProperty("ChannelId").GetGuid());
        Assert.Equal("thread-1", job.Payload.GetProperty("ExternalThreadId").GetString());
        Assert.Equal("hello", job.Payload.GetProperty("Text").GetString());
        Assert.Equal(userId, job.Payload.GetProperty("LinkedUserId").GetGuid());
        Assert.Equal(agentId, job.Payload.GetProperty("AgentId").GetGuid());
    }

    // The audit row is written BEFORE the enqueue and is threaded into the
    // payload as the worker's idempotency anchor.
    [Fact]
    public async Task InboundAuditRowIsQueuedAndAnchorsThePayload()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.SeedIdentityLink(channelId);

        await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        using var db = f.NewDb();
        var evt = await db.MessagingInboundEvents.SingleAsync();
        Assert.Equal(MessagingInboundEvent.StatusQueued, evt.Status);
        Assert.Equal("evt-1", evt.ProviderEventId);
        Assert.Null(evt.Error);

        var payloadAnchor = Assert.Single(f.Queue.Enqueued)
            .Payload.GetProperty("MessagingInboundEventId").GetGuid();
        Assert.Equal(evt.MessagingInboundEventId, payloadAnchor);
    }

    // The identity link is looked up per (channel, workspace, external user);
    // another channel's link must not authorise this one.
    [Fact]
    public async Task IdentityLinkFromAnotherChannelDoesNotAuthorise()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var otherChannelId = f.SeedChannel();
        f.SeedIdentityLink(otherChannelId);

        var outcome = await f.Build().ReceiveAsync(Provider, channelId, Request(), default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(MessagingJobTypes.Send, Assert.Single(f.Queue.Enqueued).Type);  // link prompt, not an agent turn
    }
}
