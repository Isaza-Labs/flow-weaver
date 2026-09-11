using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Messaging;

// Raw=true means Body is a verbatim response to echo (a provider verification
// challenge), not a human-facing status message.
public sealed record MessagingIngestOutcome(
    int StatusCode, string? Body = null, string ContentType = "text/plain", bool Raw = false);

// Handles an inbound webhook delivery for a messaging channel. Mirrors
// GitWebhookReceiver: the public controller has no auth context, so this creates
// a fresh DI scope and binds a synthetic identity onto it before doing any
// further work. Verifies the signature, dedupes, resolves the external
// identity (prompting account-linking when unknown), resolves/creates the
// conversation, and enqueues an agent_message job. Persists an inbound audit row
// on every outcome.
public interface IMessagingIngestService
{
    // HTTP webhook path: verifies the provider signature before processing.
    Task<MessagingIngestOutcome> ReceiveAsync(
        string provider, Guid channelId, MessagingHttpRequest request, CancellationToken ct);

    // Socket Mode path (Slack): the event arrived over an authenticated outbound
    // WebSocket, so signature verification is skipped. `body` is the raw event
    // payload (same shape as the HTTP event_callback the provider parses).
    Task<MessagingIngestOutcome> ReceiveVerifiedAsync(
        string provider, Guid channelId, byte[] body, CancellationToken ct);
}

public sealed class MessagingIngestService : IMessagingIngestService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessagingIngestService> _logger;

    public MessagingIngestService(
        IServiceScopeFactory scopeFactory, ILogger<MessagingIngestService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<MessagingIngestOutcome> ReceiveAsync(
        string provider, Guid channelId, MessagingHttpRequest request, CancellationToken ct)
        => ReceiveCoreAsync(provider, channelId, request, verifySignature: true, ct);

    public Task<MessagingIngestOutcome> ReceiveVerifiedAsync(
        string provider, Guid channelId, byte[] body, CancellationToken ct)
        => ReceiveCoreAsync(
            provider, channelId,
            new MessagingHttpRequest { Method = "POST", Body = body },
            verifySignature: false, ct);

    private async Task<MessagingIngestOutcome> ReceiveCoreAsync(
        string provider, Guid channelId, MessagingHttpRequest request, bool verifySignature, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var channels = sp.GetRequiredService<IMessagingChannelRepository>();
        var audit = sp.GetRequiredService<IMessagingDeliveryRepository>();
        var links = sp.GetRequiredService<IMessagingIdentityLinkRepository>();
        var conversations = sp.GetRequiredService<IAIConversationRepository>();
        var jobs = sp.GetRequiredService<IJobRepository>();
        var queue = sp.GetRequiredService<IQueueRepository>();
        var crypto = sp.GetRequiredService<ICredentialEncryptionService>();
        var resolver = sp.GetRequiredService<IMessagingProviderResolver>();
        var linkService = sp.GetRequiredService<IMessagingLinkService>();
        var userOverride = sp.GetRequiredService<MutableCurrentUser>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;

        var channel = await channels.FindActiveByIdAsync(channelId, ct);
        if (channel is null)
            return new MessagingIngestOutcome(404, "channel not found");
        if (!string.Equals(channel.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return new MessagingIngestOutcome(404, "channel/provider mismatch");

        // Stamp a synthetic identity on this scope so downstream writes are
        // attributed to the webhook. The receiver runs without auth headers.
        userOverride.Bind(userId: null, username: "messaging-webhook");

        var prov = resolver.Resolve(channel.Provider);
        if (prov is null)
            return new MessagingIngestOutcome(501, $"provider '{channel.Provider}' not supported");

        if (!channel.Enabled)
            return new MessagingIngestOutcome(403, "channel disabled");

        // Socket Mode delivers events over an already-authenticated WebSocket,
        // so the signature step is skipped (verifySignature == false).
        if (verifySignature)
        {
            var secret = crypto.Decrypt(channel.EncryptedSigningSecret);
            var verify = await prov.VerifyAsync(channel, request, secret, ct);
            switch (verify.Outcome)
            {
                case WebhookVerifyOutcome.Challenge:
                    return new MessagingIngestOutcome(200, verify.ChallengeBody, verify.ChallengeContentType, Raw: true);
                case WebhookVerifyOutcome.Rejected:
                    _logger.LogWarning(
                        "messaging.webhook.rejected channel={Channel} reason={Reason}",
                        channelId, verify.RejectReason);
                    return new MessagingIngestOutcome(verify.RejectStatusCode, verify.RejectReason);
            }
        }

        var inbound = prov.ParseInbound(channel, request);
        if (inbound is null)
            return new MessagingIngestOutcome(200, "no actionable message");

        // Idempotency: a provider re-delivery with the same event id is a no-op.
        if (await audit.InboundExistsAsync(channelId, inbound.ProviderEventId, ct))
            return new MessagingIngestOutcome(200, "duplicate");

        var workspace = inbound.ExternalWorkspaceId ?? string.Empty;

        // Allowlist: when set, only listed external ids may use the channel.
        if (channel.AllowedExternalIds.Count > 0
            && !channel.AllowedExternalIds.Contains(inbound.ExternalUserId, StringComparer.Ordinal))
        {
            await PersistInboundAsync(audit, channel, inbound, MessagingInboundEvent.StatusRejected,
                null, "external user not in allowlist", ct);
            return new MessagingIngestOutcome(202, "not allowed");
        }

        // Identity resolution → permissions come from the linked user's real role.
        var link = await links.FindByExternalAsync(channelId, workspace, inbound.ExternalUserId, ct);
        if (link is null)
        {
            // No linked account. Per the no-escalation rule, the agent does not
            // run for an unverified identity. Send the self-service linking link.
            if (channel.RequireLinkedUser)
            {
                var invite = await linkService.CreateLinkAsync(channel, workspace, inbound.ExternalUserId, ct);
                if (string.IsNullOrEmpty(invite.Url))
                    _logger.LogWarning(
                        "messaging.link.no_public_base_url channel_id={ChannelId} provider={Provider} — "
                        + "set Messaging:PublicBaseUrl (env MESSAGING_PUBLIC_BASE_URL) to the frontend URL so the "
                        + "bot can DM a complete /link?token=… URL.",
                        channelId, channel.Provider);
                var text = string.IsNullOrEmpty(invite.Url)
                    ? "To use this assistant, link your FlowWeaver account from the web app first."
                    : $"To use this assistant, link your FlowWeaver account: {invite.Url}";
                await EnqueueSendAsync(queue, channel, inbound.ExternalThreadId, text, null, ct);
                await PersistInboundAsync(audit, channel, inbound, MessagingInboundEvent.StatusVerified,
                    null, "unlinked — account-linking prompt sent", ct);
                return new MessagingIngestOutcome(202, "link prompt sent");
            }

            await PersistInboundAsync(audit, channel, inbound, MessagingInboundEvent.StatusRejected,
                null, "no linked user", ct);
            return new MessagingIngestOutcome(202, "no linked user");
        }

        // Resolve/create the conversation for this external thread.
        var existing = await conversations.FindByExternalThreadAsync(channelId, inbound.ExternalThreadId, ct);
        Guid conversationId;
        if (existing is null)
        {
            conversationId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            conversations.Add(new AIConversation
            {
                AIConversationId = conversationId,
                UserId = link.LinkedUserId.ToString(),
                AgentId = channel.DefaultAgentId,
                Messages = JsonSerializer.SerializeToElement(new List<object>()),
                Context = default,
                Status = "active",
                Source = channel.Provider,
                MessagingChannelId = channelId,
                ExternalThreadId = inbound.ExternalThreadId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await conversations.SaveChangesAsync(ct);
        }
        else
        {
            conversationId = existing.AIConversationId;
        }

        // Backpressure: shed load if the queue is already deep.
        var depth = await jobs.CountByStatusesAsync(new[] { JobStatus.Pending, JobStatus.Claimed }, ct);
        if (depth >= options.MaxQueueDepth)
        {
            await PersistInboundAsync(audit, channel, inbound, MessagingInboundEvent.StatusRejected,
                conversationId, $"queue full ({depth})", ct);
            return new MessagingIngestOutcome(503, "queue full, retry later");
        }

        // Dedupe gate: write the inbound "queued" row BEFORE enqueueing. The
        // unique (channel, provider_event_id) index makes a concurrent
        // re-delivery throw here, so the agent job is enqueued at most once per
        // event even when a provider double-delivers in parallel (the early
        // InboundExistsAsync check above only catches the serial case).
        Guid inboundEventId;
        try
        {
            inboundEventId = await PersistInboundAsync(audit, channel, inbound, MessagingInboundEvent.StatusQueued,
                conversationId, null, ct);
        }
        catch (DbUpdateException)
        {
            return new MessagingIngestOutcome(200, "duplicate");
        }

        var payload = MessagingJobJson.Serialize(new AgentMessagePayload
        {
            ChannelId = channelId,
            ConversationId = conversationId,
            ExternalThreadId = inbound.ExternalThreadId,
            Text = inbound.Text,
            LinkedUserId = link.LinkedUserId,
            AgentId = channel.DefaultAgentId,
            MessagingInboundEventId = inboundEventId,
        });
        await queue.EnqueueAsync(MessagingJobTypes.AgentMessage, payload, MessagingJobTypes.Tag, 0, ct);

        return new MessagingIngestOutcome(202, "queued");
    }

    private static async Task EnqueueSendAsync(
        IQueueRepository queue, MessagingChannel channel, string thread, string text,
        Guid? conversationId, CancellationToken ct)
    {
        var payload = MessagingJobJson.Serialize(new SendPayload
        {
            ChannelId = channel.MessagingChannelId,
            ExternalThreadId = thread,
            Text = text,
            ConversationId = conversationId,
        });
        await queue.EnqueueAsync(MessagingJobTypes.Send, payload, MessagingJobTypes.Tag, 0, ct);
    }

    // Returns the created inbound event id so the queued path can thread it into
    // the agent_message payload as the idempotency anchor.
    private static async Task<Guid> PersistInboundAsync(
        IMessagingDeliveryRepository audit, MessagingChannel channel, InboundMessage inbound,
        string status, Guid? conversationId, string? error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inboundEventId = Guid.NewGuid();
        audit.AddInbound(new MessagingInboundEvent
        {
            MessagingInboundEventId = inboundEventId,
            MessagingChannelId = channel.MessagingChannelId,
            ProviderEventId = inbound.ProviderEventId,
            ExternalThreadId = inbound.ExternalThreadId,
            ConversationId = conversationId,
            Status = status,
            Event = inbound.EventKind,
            Error = error,
            At = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await audit.SaveChangesAsync(ct);
        return inboundEventId;
    }
}
