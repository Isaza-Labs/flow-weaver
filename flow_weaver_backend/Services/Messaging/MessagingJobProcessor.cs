using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.Extensions.Options;
// `Job` clashes with a same-named namespace; alias to the model like
// IQueueRepository does.
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Services.Messaging;

// Processes one claimed messaging job inside a fresh DI scope. agent_message
// runs the agent as the linked user (real role, capped by the channel) and
// enqueues the reply as a messaging_send; messaging_send pushes one message to
// the provider and records a delivery row.
public interface IMessagingJobProcessor
{
    Task ProcessAsync(JobModel job, CancellationToken ct);
}

public sealed class MessagingJobProcessor : IMessagingJobProcessor
{
    private readonly IMessagingChannelRepository _channels;
    private readonly IUserRepository _users;
    private readonly IMessagingDeliveryRepository _audit;
    private readonly IMessagingProviderResolver _resolver;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IQueueRepository _queue;
    private readonly IAgentConversationRunner _runner;
    private readonly MutableCurrentUser _caller;
    private readonly IAppSettingsService _settings;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingJobProcessor> _logger;

    public MessagingJobProcessor(
        IMessagingChannelRepository channels,
        IUserRepository users,
        IMessagingDeliveryRepository audit,
        IMessagingProviderResolver resolver,
        ICredentialEncryptionService crypto,
        IQueueRepository queue,
        IAgentConversationRunner runner,
        MutableCurrentUser caller,
        IAppSettingsService settings,
        IOptions<MessagingOptions> options,
        ILogger<MessagingJobProcessor> logger)
    {
        _channels = channels;
        _users = users;
        _audit = audit;
        _resolver = resolver;
        _crypto = crypto;
        _queue = queue;
        _runner = runner;
        _caller = caller;
        _settings = settings;
        _options = options.Value;
        _logger = logger;
    }

    public Task ProcessAsync(JobModel job, CancellationToken ct) => job.Type switch
    {
        MessagingJobTypes.AgentMessage => ProcessAgentMessageAsync(job, ct),
        MessagingJobTypes.Send => ProcessSendAsync(job, ct),
        _ => LogUnknown(job),
    };

    private Task LogUnknown(JobModel job)
    {
        _logger.LogWarning("messaging.job.unknown_type type={Type} job={Job}", job.Type, job.JobId);
        return Task.CompletedTask;
    }

    private async Task ProcessAgentMessageAsync(JobModel job, CancellationToken ct)
    {
        var p = MessagingJobJson.Deserialize<AgentMessagePayload>(job.Payload);
        if (p is null) { _logger.LogWarning("messaging.job.bad_payload job={Job}", job.JobId); return; }

        // At-most-once gate: atomically claim the inbound event (queued →
        // processing). A job that is reclaimed after a crash, or a duplicate,
        // sees the row already claimed and skips — so the agent (and its LLM
        // cost + reply) never runs twice for one turn. The Guid.Empty guard keeps
        // any pre-idempotency payloads working.
        if (p.MessagingInboundEventId != Guid.Empty
            && !await _audit.TryClaimInboundForProcessingAsync(p.MessagingInboundEventId, DateTime.UtcNow, ct))
        {
            _logger.LogInformation(
                "messaging.agent.already_processed inbound={Inbound} job={Job}",
                p.MessagingInboundEventId, job.JobId);
            return;
        }

        var channel = await _channels.FindActiveByIdAsync(p.ChannelId, ct);
        if (channel is null || !channel.Enabled)
        {
            _logger.LogInformation("messaging.agent.channel_gone channel={Channel}", p.ChannelId);
            await MarkInboundAsync(p, MessagingInboundEvent.StatusFailed, ct);
            return;
        }

        var user = await _users.FindActiveByIdAsync(p.LinkedUserId, ct);
        if (user is null)
        {
            _logger.LogWarning(
                "messaging.agent.user_gone user={User} channel={Channel}", p.LinkedUserId, p.ChannelId);
            await MarkInboundAsync(p, MessagingInboundEvent.StatusFailed, ct);
            return;
        }

        // Bind the caller, never above the channel cap. In granular mode the cap
        // is a capability ceiling (the channel's MaxRole expanded to caps) that
        // EffectivePermissions intersects with the user's real grants — and that
        // rides the agent's self-call via the JWT. In legacy mode we keep the
        // capped-role binding exactly as before.
        var settings = await _settings.GetAsync(ct);
        if (RbacModes.IsGranular(settings.RbacMode))
        {
            var ceiling = string.IsNullOrWhiteSpace(channel.MaxRole)
                ? null
                : CapabilityCatalog.CapabilitiesForLegacyRole(channel.MaxRole);
            _caller.Bind(user.UserId, user.Username, new[] { user.Role }, ceiling);
        }
        else
        {
            var role = MessagingRoles.Effective(user.Role, channel.MaxRole);
            _caller.Bind(user.UserId, user.Username, new[] { role });
        }

        var result = await _runner.RunAsync(
            new AgentTurnRequest
            {
                Message = p.Text,
                ConversationId = p.ConversationId,
                AgentId = p.AgentId,
            },
            NullAgentEventSink.Instance,
            ct);

        var reply = !string.IsNullOrWhiteSpace(result.FinalText)
            ? result.FinalText
            : result.Error is not null
                ? "Sorry — something went wrong handling your request. Please try again."
                : "(no response)";

        var payload = MessagingJobJson.Serialize(new SendPayload
        {
            ChannelId = p.ChannelId,
            ExternalThreadId = p.ExternalThreadId,
            Text = reply,
            ConversationId = p.ConversationId,
        });
        await _queue.EnqueueAsync(MessagingJobTypes.Send, payload, MessagingJobTypes.Tag, 0, ct);

        // Mark the turn terminal only after the reply is enqueued, so a crash in
        // the run→enqueue gap leaves it 'processing' (the at-most-once skip still
        // prevents a double agent run on reclaim).
        await MarkInboundAsync(
            p, result.Error is null ? MessagingInboundEvent.StatusCompleted : MessagingInboundEvent.StatusFailed, ct);
    }

    // Best-effort terminal status on the inbound audit row (no-op for legacy
    // payloads without an event id). Never throws into the caller.
    private async Task MarkInboundAsync(AgentMessagePayload p, string status, CancellationToken ct)
    {
        if (p.MessagingInboundEventId == Guid.Empty) return;
        try { await _audit.MarkInboundStatusAsync(p.MessagingInboundEventId, status, DateTime.UtcNow, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "messaging.agent.mark_inbound_failed inbound={Inbound}", p.MessagingInboundEventId); }
    }

    private async Task ProcessSendAsync(JobModel job, CancellationToken ct)
    {
        var p = MessagingJobJson.Deserialize<SendPayload>(job.Payload);
        if (p is null) { _logger.LogWarning("messaging.send.bad_payload job={Job}", job.JobId); return; }

        var channel = await _channels.FindActiveByIdAsync(p.ChannelId, ct);
        if (channel is null)
        {
            _logger.LogInformation("messaging.send.channel_gone channel={Channel}", p.ChannelId);
            return;
        }

        _caller.Bind(userId: null, username: "messaging-send");

        var prov = _resolver.Resolve(channel.Provider);
        string status;
        string? error = null;
        if (prov is null)
        {
            status = MessagingDelivery.StatusFailed;
            error = $"provider '{channel.Provider}' not supported";
        }
        else
        {
            try
            {
                var token = _crypto.Decrypt(channel.EncryptedBotToken);
                await prov.SendAsync(channel, token,
                    new OutboundMessage { ExternalThreadId = p.ExternalThreadId, Text = p.Text }, ct);
                status = MessagingDelivery.StatusSent;
            }
            catch (Exception ex)
            {
                status = MessagingDelivery.StatusFailed;
                error = ex.Message;
                _logger.LogWarning(ex,
                    "messaging.send.failed channel={Channel} thread={Thread}", p.ChannelId, p.ExternalThreadId);
            }
        }

        await RecordDeliveryAsync(channel, p, status, error, p.Attempt, ct);

        // Retry transient failures up to the configured ceiling. Re-enqueue with
        // an incremented attempt; each attempt leaves its own delivery row.
        if (status == MessagingDelivery.StatusFailed)
        {
            var maxAttempts = _options.MaxSendAttempts <= 0 ? 3 : _options.MaxSendAttempts;
            if (p.Attempt < maxAttempts)
            {
                var retry = MessagingJobJson.Serialize(new SendPayload
                {
                    ChannelId = p.ChannelId,
                    ExternalThreadId = p.ExternalThreadId,
                    Text = p.Text,
                    ConversationId = p.ConversationId,
                    Attempt = p.Attempt + 1,
                });
                await _queue.EnqueueAsync(MessagingJobTypes.Send, retry, MessagingJobTypes.Tag, 0, ct);
                _logger.LogInformation(
                    "messaging.send.retry channel={Channel} next_attempt={Attempt}/{Max}",
                    p.ChannelId, p.Attempt + 1, maxAttempts);
            }
            else
            {
                _logger.LogWarning(
                    "messaging.send.exhausted channel={Channel} attempts={Attempts}", p.ChannelId, p.Attempt);
            }
        }
    }

    private async Task RecordDeliveryAsync(
        MessagingChannel channel, SendPayload p, string status, string? error, int attempt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _audit.AddDelivery(new MessagingDelivery
        {
            MessagingDeliveryId = Guid.NewGuid(),
            MessagingChannelId = channel.MessagingChannelId,
            ConversationId = p.ConversationId,
            ExternalThreadId = p.ExternalThreadId,
            Status = status,
            Attempt = attempt,
            Error = error,
            At = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var tracked = await _channels.FindTrackedByIdAsync(channel.MessagingChannelId, ct);
        if (tracked is not null)
        {
            tracked.LastDeliveryAt = now;
            tracked.LastDeliveryStatus = status;
            tracked.UpdatedAt = now;
        }
        await _audit.SaveChangesAsync(ct);
    }
}
