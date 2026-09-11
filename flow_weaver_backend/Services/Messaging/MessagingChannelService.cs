using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Messaging;

// Admin CRUD for messaging channels + their audit/identity-link views. Secrets
// are encrypted on write and never returned (only has_* booleans).
public interface IMessagingChannelService
{
    Task<ActionResult<ListResponse<MessagingChannelResponse>>> ListAsync(int limit, int offset);
    Task<ActionResult<MessagingChannelResponse>> GetAsync(Guid id);
    Task<ActionResult<MessagingChannelResponse>> CreateAsync(CreateMessagingChannel dto);
    Task<ActionResult<MessagingChannelResponse>> UpdateAsync(Guid id, UpdateMessagingChannel dto);
    Task<IActionResult> DeleteAsync(Guid id);
    Task<ActionResult<MessagingChannelActivityResponse>> GetActivityAsync(Guid id, int limit);
    Task<ActionResult<ListResponse<MessagingIdentityLinkResponse>>> ListLinksAsync(Guid id);
    Task<IActionResult> RevokeLinkAsync(Guid id, Guid linkId);
}

public class MessagingChannelService : IMessagingChannelService
{
    private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        MessagingChannel.ProviderTelegram, MessagingChannel.ProviderSlack,
        MessagingChannel.ProviderWhatsApp, MessagingChannel.ProviderTeams,
    };
    private static readonly HashSet<string> KnownRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "viewer", "operator", "admin",
    };

    private readonly IMessagingChannelRepository _channels;
    private readonly IMessagingDeliveryRepository _audit;
    private readonly IMessagingIdentityLinkRepository _links;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ICurrentUser _caller;
    private readonly MessagingOptions _options;
    // Named _auditLog, not _audit: this type already uses _audit for the
    // per-channel DELIVERY history, which is a different thing entirely.
    private readonly IAuditLogger _auditLog;

    public MessagingChannelService(
        IMessagingChannelRepository channels,
        IMessagingDeliveryRepository audit,
        IMessagingIdentityLinkRepository links,
        ICredentialEncryptionService crypto,
        ICurrentUser caller,
        IOptions<MessagingOptions> options,
        IAuditLogger auditLog)
    {
        _channels = channels;
        _audit = audit;
        _links = links;
        _crypto = crypto;
        _caller = caller;
        _options = options.Value;
        _auditLog = auditLog;
    }

    // Shared audit projection. A channel is an authenticated entry point into
    // the platform: MaxRole caps what a chat user can do, RequireLinkedUser and
    // AllowedExternalIds decide who may talk to it at all, and AllowUnsigned
    // turns off signature checks on the inbound webhook. Those four are the
    // security posture of the channel and none of them were audited. The three
    // encrypted tokens never appear here.
    private static object ChannelAudit(MessagingChannel c) => new
    {
        c.Name,
        c.Provider,
        c.Enabled,
        max_role = c.MaxRole,
        require_linked_user = c.RequireLinkedUser,
        allow_unsigned = c.AllowUnsigned,
        allowed_external_id_count = c.AllowedExternalIds?.Count ?? 0,
        default_agent_id = c.DefaultAgentId,
    };

    public async Task<ActionResult<ListResponse<MessagingChannelResponse>>> ListAsync(int limit, int offset)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _channels.CountAsync();
        var rows = await _channels.ListAsync(limit, offset);
        return new OkObjectResult(new ListResponse<MessagingChannelResponse>
        {
            Data = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<MessagingChannelResponse>> GetAsync(Guid id)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: false)
            ?? throw new NotFoundException("messaging channel", id);
        return ToResponse(channel);
    }

    public async Task<ActionResult<MessagingChannelResponse>> CreateAsync(CreateMessagingChannel dto)
    {
        ValidateProvider(dto.Provider);
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ValidationException("name is required", code: "name_required");
        ValidateMaxRole(dto.MaxRole);
        ValidateProviderConfig(dto.Provider, dto.ExternalConfig ?? default);

        var now = DateTime.UtcNow;
        var channel = new MessagingChannel
        {
            MessagingChannelId = Guid.NewGuid(),
            Provider = dto.Provider.ToLowerInvariant(),
            Name = dto.Name.Trim(),
            EncryptedBotToken = string.IsNullOrEmpty(dto.BotToken) ? null : _crypto.Encrypt(dto.BotToken),
            EncryptedSigningSecret = string.IsNullOrEmpty(dto.SigningSecret) ? null : _crypto.Encrypt(dto.SigningSecret),
            EncryptedAppToken = string.IsNullOrEmpty(dto.AppToken) ? null : _crypto.Encrypt(dto.AppToken),
            ExternalConfig = dto.ExternalConfig ?? default,
            DefaultAgentId = dto.DefaultAgentId,
            MaxRole = NormalizeRole(dto.MaxRole),
            RequireLinkedUser = dto.RequireLinkedUser ?? true,
            AllowedExternalIds = dto.AllowedExternalIds ?? new(),
            AllowUnsigned = dto.AllowUnsigned ?? false,
            Enabled = dto.Enabled ?? true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _channels.Add(channel);
        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("messaging_channel", channel.MessagingChannelId, "create",
            after: ChannelAudit(channel));

        return ToResponse(channel);
    }

    public async Task<ActionResult<MessagingChannelResponse>> UpdateAsync(Guid id, UpdateMessagingChannel dto)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("messaging channel", id);

        // Snapshot before the dto lands (`channel` is tracked).
        var auditBefore = ChannelAudit(channel);

        if (!string.IsNullOrWhiteSpace(dto.Provider))
        {
            ValidateProvider(dto.Provider);
            channel.Provider = dto.Provider.ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(dto.Name)) channel.Name = dto.Name.Trim();
        // A non-null secret rotates it; null leaves it untouched.
        if (dto.BotToken is not null)
            channel.EncryptedBotToken = dto.BotToken.Length == 0 ? null : _crypto.Encrypt(dto.BotToken);
        if (dto.SigningSecret is not null)
            channel.EncryptedSigningSecret = dto.SigningSecret.Length == 0 ? null : _crypto.Encrypt(dto.SigningSecret);
        if (dto.AppToken is not null)
            channel.EncryptedAppToken = dto.AppToken.Length == 0 ? null : _crypto.Encrypt(dto.AppToken);
        if (dto.ExternalConfig is not null) channel.ExternalConfig = dto.ExternalConfig.Value;
        if (dto.DefaultAgentId is not null) channel.DefaultAgentId = dto.DefaultAgentId;
        if (dto.MaxRole is not null)
        {
            ValidateMaxRole(dto.MaxRole);
            channel.MaxRole = NormalizeRole(dto.MaxRole);
        }
        if (dto.RequireLinkedUser is not null) channel.RequireLinkedUser = dto.RequireLinkedUser.Value;
        if (dto.AllowedExternalIds is not null) channel.AllowedExternalIds = dto.AllowedExternalIds;
        if (dto.AllowUnsigned is not null) channel.AllowUnsigned = dto.AllowUnsigned.Value;
        if (dto.Enabled is not null) channel.Enabled = dto.Enabled.Value;
        // Validated against the merged result, not the dto: switching provider
        // and supplying the config are two independent fields of one request.
        ValidateProviderConfig(channel.Provider, channel.ExternalConfig);
        channel.UpdatedAt = DateTime.UtcNow;

        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("messaging_channel", channel.MessagingChannelId, "update",
            before: auditBefore,
            after: new
            {
                channel.Name,
                channel.Provider,
                channel.Enabled,
                max_role = channel.MaxRole,
                require_linked_user = channel.RequireLinkedUser,
                allow_unsigned = channel.AllowUnsigned,
                allowed_external_id_count = channel.AllowedExternalIds?.Count ?? 0,
                default_agent_id = channel.DefaultAgentId,
                bot_token_rotated = dto.BotToken is not null,
                signing_secret_rotated = dto.SigningSecret is not null,
                app_token_rotated = dto.AppToken is not null,
            });

        return ToResponse(channel);
    }

    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var channel = await _channels.GetByIdAsync(id, tracking: true)
            ?? throw new NotFoundException("messaging channel", id);
        channel.IsActive = false;
        channel.Enabled = false;
        channel.UpdatedAt = DateTime.UtcNow;
        await _channels.SaveChangesAsync();

        await _auditLog.LogAsync("messaging_channel", channel.MessagingChannelId, "delete",
            before: ChannelAudit(channel));

        return new NoContentResult();
    }

    public async Task<ActionResult<MessagingChannelActivityResponse>> GetActivityAsync(Guid id, int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        // Existence check before loading the audit views.
        _ = await _channels.GetByIdAsync(id, tracking: false)
            ?? throw new NotFoundException("messaging channel", id);

        var inbound = await _audit.ListInboundAsync(id, limit);
        var outbound = await _audit.ListDeliveriesAsync(id, limit);
        return new MessagingChannelActivityResponse
        {
            Inbound = inbound.Select(e => new MessagingInboundEventResponse
            {
                MessagingInboundEventId = e.MessagingInboundEventId,
                ProviderEventId = e.ProviderEventId,
                ExternalThreadId = e.ExternalThreadId,
                ConversationId = e.ConversationId,
                Status = e.Status,
                Event = e.Event,
                Error = e.Error,
                At = e.At,
            }).ToList(),
            Outbound = outbound.Select(d => new MessagingDeliveryResponse
            {
                MessagingDeliveryId = d.MessagingDeliveryId,
                ConversationId = d.ConversationId,
                ExternalThreadId = d.ExternalThreadId,
                Status = d.Status,
                Attempt = d.Attempt,
                Error = d.Error,
                At = d.At,
            }).ToList(),
        };
    }

    public async Task<ActionResult<ListResponse<MessagingIdentityLinkResponse>>> ListLinksAsync(Guid id)
    {
        _ = await _channels.GetByIdAsync(id, tracking: false)
            ?? throw new NotFoundException("messaging channel", id);
        var links = await _links.ListByChannelAsync(id);
        var data = links.Select(l => new MessagingIdentityLinkResponse
        {
            MessagingIdentityLinkId = l.MessagingIdentityLinkId,
            ExternalWorkspaceId = l.ExternalWorkspaceId,
            ExternalUserId = l.ExternalUserId,
            LinkedUserId = l.LinkedUserId,
            DisplayName = l.DisplayName,
            CreatedAt = l.CreatedAt,
        }).ToList();
        return new OkObjectResult(new ListResponse<MessagingIdentityLinkResponse>
        {
            Data = data,
            Total = data.Count,
            Limit = data.Count,
            Offset = 0,
        });
    }

    public async Task<IActionResult> RevokeLinkAsync(Guid id, Guid linkId)
    {
        var link = await _links.FindTrackedByIdAsync(linkId);
        if (link is null || link.MessagingChannelId != id)
            throw new NotFoundException("messaging identity link", linkId);
        link.IsActive = false;
        link.UpdatedAt = DateTime.UtcNow;
        await _links.SaveChangesAsync();
        return new NoContentResult();
    }

    private static void ValidateProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || !KnownProviders.Contains(provider))
            throw new ValidationException(
                $"provider must be one of: {string.Join(", ", KnownProviders)}",
                code: "invalid_provider");
    }

    // Provider-specific required entries in external_config. A Teams channel
    // without an app_id is not merely incomplete: app_id is the audience the
    // inbound Bot Framework token is validated against and the client_id the
    // reply is minted with, so every message would 401 in both directions with
    // nothing but the delivery log to explain it.
    private static void ValidateProviderConfig(string provider, JsonElement config)
    {
        if (!string.Equals(provider, MessagingChannel.ProviderTeams, StringComparison.OrdinalIgnoreCase))
            return;

        var hasAppId = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("app_id", out var appId)
            && appId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(appId.GetString());
        if (!hasAppId)
            throw new ValidationException(
                "teams channels need an \"app_id\" (the Azure Bot's Microsoft App ID) in external_config",
                code: "teams_app_id_required");
    }

    private static void ValidateMaxRole(string? role)
    {
        if (role is not null && role.Length > 0 && !KnownRoles.Contains(role))
            throw new ValidationException(
                $"max_role must be one of: {string.Join(", ", KnownRoles)}",
                code: "invalid_max_role");
    }

    private static string? NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null : role.ToLowerInvariant();

    private MessagingChannelResponse ToResponse(MessagingChannel c) => new()
    {
        MessagingChannelId = c.MessagingChannelId,
        Provider = c.Provider,
        Name = c.Name,
        HasBotToken = c.EncryptedBotToken is { Length: > 0 },
        HasSigningSecret = c.EncryptedSigningSecret is { Length: > 0 },
        HasAppToken = c.EncryptedAppToken is { Length: > 0 },
        ExternalConfig = c.ExternalConfig,
        DefaultAgentId = c.DefaultAgentId,
        MaxRole = c.MaxRole,
        RequireLinkedUser = c.RequireLinkedUser,
        AllowedExternalIds = c.AllowedExternalIds,
        AllowUnsigned = c.AllowUnsigned,
        Enabled = c.Enabled,
        WebhookUrl = BuildWebhookUrl(c.Provider, c.MessagingChannelId),
        LastDeliveryAt = c.LastDeliveryAt,
        LastDeliveryStatus = c.LastDeliveryStatus,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };

    private string BuildWebhookUrl(string provider, Guid channelId)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/messaging/webhooks/{provider}/{channelId}";
    }
}
