using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Messaging;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Tests;

// The worker side of messaging. Two invariants dominate: the agent runs
// AT MOST ONCE per inbound turn (a reclaimed job must not re-bill an LLM call
// or double-reply), and the identity it runs as is the linked user's real role
// capped by the channel — a channel can only ever narrow privileges.
public class MessagingJobProcessorTests
{

    private sealed class ScriptedRunner : IAgentConversationRunner
    {
        public string FinalText { get; set; } = "the agent reply";
        public string? Error { get; set; }
        public int Runs { get; private set; }
        public List<(Guid UserId, IReadOnlyList<string> Roles, IReadOnlyCollection<string>? Ceiling)> BoundAs { get; } = new();
        private readonly ICurrentUser _caller;

        public ScriptedRunner(ICurrentUser caller) => _caller = caller;

        public Task<AgentTurnResult> RunAsync(AgentTurnRequest request, IAgentEventSink sink, CancellationToken ct)
        {
            Runs++;
            // Capture the identity in force at run time — that's what the
            // no-escalation rule is actually about.
            BoundAs.Add((_caller.UserId, _caller.Roles, _caller.CapabilityCeiling));
            return Task.FromResult(new AgentTurnResult
            {
                ConversationId = request.ConversationId ?? Guid.NewGuid(),
                IsNewConversation = false,
                FinalText = FinalText,
                Error = Error,
            });
        }
    }

    private sealed class ScriptedProvider : IMessagingProvider
    {
        public string Provider { get; init; } = "telegram";
        public Exception? ThrowOnSend { get; set; }
        public List<OutboundMessage> Sent { get; } = new();
        public string? TokenSeen { get; private set; }

        public Task<WebhookVerifyResult> VerifyAsync(MessagingChannel c, MessagingHttpRequest r, string? s, CancellationToken ct)
            => Task.FromResult(WebhookVerifyResult.Verified());
        public InboundMessage? ParseInbound(MessagingChannel c, MessagingHttpRequest r) => null;
        public Task SendAsync(MessagingChannel channel, string? decryptedBotToken, OutboundMessage message, CancellationToken ct)
        {
            TokenSeen = decryptedBotToken;
            if (ThrowOnSend is not null) throw ThrowOnSend;
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingQueue : IQueueRepository
    {
        public List<(string Type, JsonElement Payload)> Enqueued { get; } = new();
        public Task<Guid> EnqueueAsync(string type, JsonElement payload, string tag, int priority, CancellationToken ct)
        {
            Enqueued.Add((type, payload));
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<JobModel?> ClaimAsync(string[] tags, string workerId, CancellationToken ct) => Task.FromResult<JobModel?>(null);
        public Task CompleteAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(Guid jobId, string error, CancellationToken ct) => Task.CompletedTask;
        public Task<int> ReclaimExpiredAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct) => Task.FromResult(true);
        public Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct) => Task.FromResult(0);
    }

    // The real repository claims the inbound row with ExecuteUpdateAsync, which
    // the InMemory provider does not implement. This subclass keeps every other
    // member and swaps only the two set-based
    // statements for tracked-entity equivalents. The atomicity those statements
    // buy is a Postgres concern and belongs to integration tests; what these
    // tests assert is the processor's decision to skip on a lost claim.
    private sealed class InMemoryClaimDeliveryRepository : MessagingDeliveryRepository
    {
        private readonly AppDbContext _db;
        public InMemoryClaimDeliveryRepository(AppDbContext db) : base(db) => _db = db;

        public override async Task<bool> TryClaimInboundForProcessingAsync(
            Guid inboundEventId, DateTime nowUtc, CancellationToken ct = default)
        {
            var row = await _db.Set<MessagingInboundEvent>()
                .FirstOrDefaultAsync(e => e.MessagingInboundEventId == inboundEventId
                                          && e.Status == MessagingInboundEvent.StatusQueued, ct);
            if (row is null) return false;
            row.Status = MessagingInboundEvent.StatusProcessing;
            row.UpdatedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public override async Task MarkInboundStatusAsync(
            Guid inboundEventId, string status, DateTime nowUtc, CancellationToken ct = default)
        {
            var row = await _db.Set<MessagingInboundEvent>()
                .FirstOrDefaultAsync(e => e.MessagingInboundEventId == inboundEventId, ct);
            if (row is null) return;
            row.Status = status;
            row.UpdatedAt = nowUtc;
            await _db.SaveChangesAsync(ct);
        }
    }

    private sealed class FixedSettings : IAppSettingsService
    {
        private readonly AppSettings _settings;
        public FixedSettings(string rbacMode) => _settings = new AppSettings { RbacMode = rbacMode };
        public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(_settings);
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default) => Task.FromResult(updated);
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ScriptedProvider Provider { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public MutableCurrentUser Caller { get; } = new(new HttpContextAccessor());
        public ScriptedRunner Runner { get; }
        private readonly string _rbacMode;

        public Fixture(string rbacMode = "legacy")
        {
            _rbacMode = rbacMode;
            Runner = new ScriptedRunner(Caller);
        }

        public MessagingJobProcessor Build(MessagingOptions? options = null) => new(
            new MessagingChannelRepository(Db),
            new UserRepository(Db),
            new InMemoryClaimDeliveryRepository(Db),
            new MessagingProviderResolver(new IMessagingProvider[] { Provider }),
            new FakeCrypto(),
            Queue,
            Runner,
            Caller,
            new FixedSettings(_rbacMode),
            Options.Create(options ?? new MessagingOptions()),
            NullLogger<MessagingJobProcessor>.Instance);

        public Guid SeedChannel(
            string provider = "telegram", bool enabled = true,
            string? maxRole = null, string? botToken = "bot-token")
        {
            var id = Guid.NewGuid();
            Db.MessagingChannels.Add(new MessagingChannel
            {
                MessagingChannelId = id,
                Provider = provider,
                Name = "chan",
                Enabled = enabled,
                MaxRole = maxRole,
                EncryptedBotToken = botToken is null ? null : Encoding.UTF8.GetBytes(botToken),
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedUser(string role = "admin")
        {
            var id = Guid.NewGuid();
            Db.Set<User>().Add(new User
            {
                UserId = id,
                Username = "linked-user",
                Email = "u@example.com",
                Role = role,
                PasswordHash = "x",
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedInboundEvent(Guid channelId, string status = MessagingInboundEvent.StatusQueued)
        {
            var id = Guid.NewGuid();
            Db.MessagingInboundEvents.Add(new MessagingInboundEvent
            {
                MessagingInboundEventId = id,
                MessagingChannelId = channelId,
                ProviderEventId = "evt-1",
                ExternalThreadId = "thread-1",
                Status = status,
                At = DateTime.UtcNow,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static JobModel Job(string type, object payload) => new()
    {
        JobId = Guid.NewGuid(),
        Type = type,
        Tag = MessagingJobTypes.Tag,
        Payload = JsonSerializer.SerializeToElement(payload),
        IsActive = true,
    };

    private static JobModel AgentJob(
        Guid channelId, Guid userId, Guid inboundEventId,
        Guid? conversationId = null, Guid? agentId = null, string text = "hello")
        => Job(MessagingJobTypes.AgentMessage, new AgentMessagePayload
        {
            ChannelId = channelId,
            ConversationId = conversationId ?? Guid.NewGuid(),
            ExternalThreadId = "thread-1",
            Text = text,
            LinkedUserId = userId,
            AgentId = agentId,
            MessagingInboundEventId = inboundEventId,
        });

    private static JobModel SendJob(Guid channelId, string text = "reply", int attempt = 1)
        => Job(MessagingJobTypes.Send, new SendPayload
        {
            ChannelId = channelId,
            ExternalThreadId = "thread-1",
            Text = text,
            Attempt = attempt,
        });

    // ─── dispatch ───────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownJobTypeIsIgnored()
    {
        using var f = new Fixture();

        await f.Build().ProcessAsync(Job("something_else", new { }), default);

        Assert.Equal(0, f.Runner.Runs);
        Assert.Empty(f.Queue.Enqueued);
    }

    // A JSON `null` payload deserialises to null and is logged and dropped.
    [Fact]
    public async Task NullAgentPayloadIsIgnored()
    {
        using var f = new Fixture();
        var job = Job(MessagingJobTypes.AgentMessage, new { });
        job.Payload = TestJson.Element("null");

        await f.Build().ProcessAsync(job, default);

        Assert.Equal(0, f.Runner.Runs);
    }

    [Fact]
    public async Task NullSendPayloadIsIgnored()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var job = Job(MessagingJobTypes.Send, new { });
        job.Payload = TestJson.Element("null");

        await f.Build().ProcessAsync(job, default);

        Assert.Empty(f.Provider.Sent);
    }

    // A payload of the WRONG SHAPE (a string where an object is expected) is not
    // caught by the `is null` guard — System.Text.Json throws first, so the job
    // fails and the worker's retry/fail handling takes over. Pinned because the
    // `bad_payload` log line suggests it would be swallowed, and it isn't.
    [Fact]
    public async Task WrongShapedPayloadPropagatesInsteadOfBeingSwallowed()
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<JsonException>(
            () => f.Build().ProcessAsync(Job(MessagingJobTypes.AgentMessage, "not-an-object"), default));

        Assert.Equal(0, f.Runner.Runs);
    }

    // ─── at-most-once ───────────────────────────────────────────────────

    // The whole point of the inbound-event claim: a reclaimed job (worker crash,
    // lease expiry) must not re-run the agent — that would double-bill the LLM
    // and double-reply to the user.
    [Fact]
    public async Task ReprocessingTheSameTurnDoesNotRunTheAgentTwice()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);
        var job = AgentJob(channelId, userId, inboundId);
        var svc = f.Build();

        await svc.ProcessAsync(job, default);
        await svc.ProcessAsync(job, default);

        Assert.Equal(1, f.Runner.Runs);
        Assert.Single(f.Queue.Enqueued);
    }

    // An inbound row already past 'queued' was claimed by another worker.
    [Fact]
    public async Task AlreadyClaimedInboundEventIsSkipped()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId, MessagingInboundEvent.StatusProcessing);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(0, f.Runner.Runs);
    }

    // Payloads predating the idempotency anchor must still work.
    [Fact]
    public async Task LegacyPayloadWithoutInboundIdStillRuns()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();

        await f.Build().ProcessAsync(AgentJob(channelId, userId, Guid.Empty), default);

        Assert.Equal(1, f.Runner.Runs);
    }

    // ─── guards before running the agent ────────────────────────────────

    [Fact]
    public async Task DeletedChannelFailsTheTurnWithoutRunningTheAgent()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);
        f.Db.MessagingChannels.Single().IsActive = false;
        await f.Db.SaveChangesAsync();

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(0, f.Runner.Runs);
        Assert.Equal(MessagingInboundEvent.StatusFailed,
            (await f.Db.MessagingInboundEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task DisabledChannelFailsTheTurn()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(enabled: false);
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(0, f.Runner.Runs);
    }

    [Fact]
    public async Task DeletedLinkedUserFailsTheTurn()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, Guid.NewGuid(), inboundId), default);

        Assert.Equal(0, f.Runner.Runs);
        Assert.Equal(MessagingInboundEvent.StatusFailed,
            (await f.Db.MessagingInboundEvents.SingleAsync()).Status);
    }

    // ─── identity binding (a channel narrows, never widens) ─────────────

    [Fact]
    public async Task LegacyMode_BindsTheUsersOwnRoleWhenChannelHasNoCap()
    {
        using var f = new Fixture("legacy");
        var channelId = f.SeedChannel(maxRole: null);
        var userId = f.SeedUser(role: "operator");
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        var bound = Assert.Single(f.Runner.BoundAs);
        Assert.Equal(userId, bound.UserId);
        Assert.Equal(new[] { "operator" }, bound.Roles);
    }

    // The channel cap must lower an admin to the capped role.
    [Fact]
    public async Task LegacyMode_ChannelCapNarrowsAnAdmin()
    {
        using var f = new Fixture("legacy");
        var channelId = f.SeedChannel(maxRole: "viewer");
        var userId = f.SeedUser(role: "admin");
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(new[] { "viewer" }, Assert.Single(f.Runner.BoundAs).Roles);
    }

    // ...but it must never raise a viewer to the cap.
    [Fact]
    public async Task LegacyMode_ChannelCapDoesNotPromote()
    {
        using var f = new Fixture("legacy");
        var channelId = f.SeedChannel(maxRole: "admin");
        var userId = f.SeedUser(role: "viewer");
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(new[] { "viewer" }, Assert.Single(f.Runner.BoundAs).Roles);
    }

    // In granular mode the cap rides as a capability ceiling instead of a role
    // swap, so EffectivePermissions can intersect it with the user's grants.
    [Fact]
    public async Task GranularMode_BindsRealRolePlusCapabilityCeiling()
    {
        using var f = new Fixture(RbacModes.Granular);
        var channelId = f.SeedChannel(maxRole: "viewer");
        var userId = f.SeedUser(role: "admin");
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        var bound = Assert.Single(f.Runner.BoundAs);
        Assert.Equal(new[] { "admin" }, bound.Roles);   // real role preserved
        Assert.NotNull(bound.Ceiling);                  // ...capped by capabilities
        Assert.NotEmpty(bound.Ceiling!);
    }

    [Fact]
    public async Task GranularMode_NoChannelCapMeansNoCeiling()
    {
        using var f = new Fixture(RbacModes.Granular);
        var channelId = f.SeedChannel(maxRole: null);
        var userId = f.SeedUser(role: "operator");
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Null(Assert.Single(f.Runner.BoundAs).Ceiling);
    }

    // ─── reply enqueue + terminal status ────────────────────────────────

    [Fact]
    public async Task AgentReplyIsEnqueuedAsASendJob()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);
        var conversationId = Guid.NewGuid();
        f.Runner.FinalText = "here are your devices";

        await f.Build().ProcessAsync(
            AgentJob(channelId, userId, inboundId, conversationId), default);

        var job = Assert.Single(f.Queue.Enqueued);
        Assert.Equal(MessagingJobTypes.Send, job.Type);
        Assert.Equal("here are your devices", job.Payload.GetProperty("Text").GetString());
        Assert.Equal("thread-1", job.Payload.GetProperty("ExternalThreadId").GetString());
        Assert.Equal(conversationId, job.Payload.GetProperty("ConversationId").GetGuid());
    }

    [Fact]
    public async Task SuccessfulTurnMarksTheInboundCompleted()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal(MessagingInboundEvent.StatusCompleted,
            (await f.Db.MessagingInboundEvents.SingleAsync()).Status);
    }

    // An agent error still owes the user a reply — silence looks like a hang.
    [Fact]
    public async Task AgentErrorStillRepliesAndMarksFailed()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);
        f.Runner.Error = "llm exploded";
        f.Runner.FinalText = string.Empty;

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        var job = Assert.Single(f.Queue.Enqueued);
        Assert.Contains("something went wrong", job.Payload.GetProperty("Text").GetString());
        Assert.Equal(MessagingInboundEvent.StatusFailed,
            (await f.Db.MessagingInboundEvents.SingleAsync()).Status);
    }

    // A silent agent gets a placeholder rather than an empty message.
    [Fact]
    public async Task EmptyAgentReplyBecomesAPlaceholder()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        var userId = f.SeedUser();
        var inboundId = f.SeedInboundEvent(channelId);
        f.Runner.FinalText = "   ";

        await f.Build().ProcessAsync(AgentJob(channelId, userId, inboundId), default);

        Assert.Equal("(no response)",
            Assert.Single(f.Queue.Enqueued).Payload.GetProperty("Text").GetString());
    }

    // ─── send path ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendPushesToTheProviderWithTheDecryptedToken()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(botToken: "secret-bot-token");

        await f.Build().ProcessAsync(SendJob(channelId, "hi there"), default);

        var sent = Assert.Single(f.Provider.Sent);
        Assert.Equal("hi there", sent.Text);
        Assert.Equal("thread-1", sent.ExternalThreadId);
        Assert.Equal("secret-bot-token", f.Provider.TokenSeen);
    }

    [Fact]
    public async Task SuccessfulSendRecordsADeliveryAndStampsTheChannel()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();

        await f.Build().ProcessAsync(SendJob(channelId), default);

        var delivery = await f.Db.MessagingDeliveries.SingleAsync();
        Assert.Equal(MessagingDelivery.StatusSent, delivery.Status);
        Assert.Equal(1, delivery.Attempt);
        Assert.Null(delivery.Error);

        var channel = await f.Db.MessagingChannels.SingleAsync();
        Assert.Equal(MessagingDelivery.StatusSent, channel.LastDeliveryStatus);
        Assert.NotNull(channel.LastDeliveryAt);
    }

    [Fact]
    public async Task MissingChannelSkipsTheSend()
    {
        using var f = new Fixture();

        await f.Build().ProcessAsync(SendJob(Guid.NewGuid()), default);

        Assert.Empty(f.Provider.Sent);
        Assert.Empty(f.Db.MessagingDeliveries);
    }

    [Fact]
    public async Task UnsupportedProviderIsRecordedAsAFailedDelivery()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel(provider: "discord");

        await f.Build().ProcessAsync(SendJob(channelId), default);

        var delivery = await f.Db.MessagingDeliveries.SingleAsync();
        Assert.Equal(MessagingDelivery.StatusFailed, delivery.Status);
        Assert.Contains("not supported", delivery.Error);
    }

    // ─── send retries ───────────────────────────────────────────────────

    [Fact]
    public async Task FailedSendIsRetriedWithAnIncrementedAttempt()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.ThrowOnSend = new InvalidOperationException("telegram 502");

        await f.Build(new MessagingOptions { MaxSendAttempts = 3 })
            .ProcessAsync(SendJob(channelId, attempt: 1), default);

        var retry = Assert.Single(f.Queue.Enqueued);
        Assert.Equal(MessagingJobTypes.Send, retry.Type);
        Assert.Equal(2, retry.Payload.GetProperty("Attempt").GetInt32());

        var delivery = await f.Db.MessagingDeliveries.SingleAsync();
        Assert.Equal(MessagingDelivery.StatusFailed, delivery.Status);
        Assert.Contains("telegram 502", delivery.Error);
    }

    // Each attempt leaves its own delivery row, so the trail shows every try.
    [Fact]
    public async Task RetryPreservesTheMessageAndThread()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.ThrowOnSend = new InvalidOperationException("boom");

        await f.Build().ProcessAsync(SendJob(channelId, "important reply"), default);

        var retry = Assert.Single(f.Queue.Enqueued);
        Assert.Equal("important reply", retry.Payload.GetProperty("Text").GetString());
        Assert.Equal("thread-1", retry.Payload.GetProperty("ExternalThreadId").GetString());
    }

    // At the ceiling we stop retrying instead of looping forever.
    [Fact]
    public async Task ExhaustedAttemptsStopRetrying()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.ThrowOnSend = new InvalidOperationException("still down");

        await f.Build(new MessagingOptions { MaxSendAttempts = 3 })
            .ProcessAsync(SendJob(channelId, attempt: 3), default);

        Assert.Empty(f.Queue.Enqueued);
        Assert.Equal(3, (await f.Db.MessagingDeliveries.SingleAsync()).Attempt);
    }

    // A non-positive configured ceiling falls back to 3 rather than disabling
    // retries entirely.
    [Fact]
    public async Task NonPositiveMaxAttemptsFallsBackToThree()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();
        f.Provider.ThrowOnSend = new InvalidOperationException("down");

        await f.Build(new MessagingOptions { MaxSendAttempts = 0 })
            .ProcessAsync(SendJob(channelId, attempt: 2), default);

        Assert.Single(f.Queue.Enqueued);   // 2 < 3, so it retries
    }

    [Fact]
    public async Task SuccessfulSendDoesNotRetry()
    {
        using var f = new Fixture();
        var channelId = f.SeedChannel();

        await f.Build().ProcessAsync(SendJob(channelId), default);

        Assert.Empty(f.Queue.Enqueued);
    }
}
