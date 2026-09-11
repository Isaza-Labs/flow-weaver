using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos.Messaging;

// ─── Channel CRUD ───────────────────────────────────────────────────

public class CreateMessagingChannel
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // Plaintext secrets — encrypted at the service layer, never returned.
    [JsonPropertyName("bot_token")]
    public string? BotToken { get; set; }

    [JsonPropertyName("signing_secret")]
    public string? SigningSecret { get; set; }

    // Slack App-Level Token (xapp-…) — enables Socket Mode (outbound WebSocket,
    // no public webhook). Only meaningful for the slack provider.
    [JsonPropertyName("app_token")]
    public string? AppToken { get; set; }

    [JsonPropertyName("external_config")]
    public JsonElement? ExternalConfig { get; set; }

    [JsonPropertyName("default_agent_id")]
    public Guid? DefaultAgentId { get; set; }

    [JsonPropertyName("max_role")]
    public string? MaxRole { get; set; }

    [JsonPropertyName("require_linked_user")]
    public bool? RequireLinkedUser { get; set; }

    [JsonPropertyName("allowed_external_ids")]
    public List<string>? AllowedExternalIds { get; set; }

    [JsonPropertyName("allow_unsigned")]
    public bool? AllowUnsigned { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}

// Update reuses the create shape; null fields are left unchanged. A non-null
// (even empty) bot_token / signing_secret rotates the secret.
public class UpdateMessagingChannel : CreateMessagingChannel
{
}

public class MessagingChannelResponse
{
    [JsonPropertyName("messaging_channel_id")]
    public Guid MessagingChannelId { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // Booleans instead of the secrets themselves.
    [JsonPropertyName("has_bot_token")]
    public bool HasBotToken { get; set; }

    [JsonPropertyName("has_signing_secret")]
    public bool HasSigningSecret { get; set; }

    [JsonPropertyName("has_app_token")]
    public bool HasAppToken { get; set; }

    [JsonPropertyName("external_config")]
    public JsonElement ExternalConfig { get; set; } = default;

    [JsonPropertyName("default_agent_id")]
    public Guid? DefaultAgentId { get; set; }

    [JsonPropertyName("max_role")]
    public string? MaxRole { get; set; }

    [JsonPropertyName("require_linked_user")]
    public bool RequireLinkedUser { get; set; }

    [JsonPropertyName("allowed_external_ids")]
    public List<string> AllowedExternalIds { get; set; } = new();

    [JsonPropertyName("allow_unsigned")]
    public bool AllowUnsigned { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    // Public URL to register in the provider's webhook settings.
    [JsonPropertyName("webhook_url")]
    public string WebhookUrl { get; set; } = string.Empty;

    [JsonPropertyName("last_delivery_at")]
    public DateTime? LastDeliveryAt { get; set; }

    [JsonPropertyName("last_delivery_status")]
    public string? LastDeliveryStatus { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

// ─── Audit views ────────────────────────────────────────────────────

public class MessagingInboundEventResponse
{
    [JsonPropertyName("messaging_inbound_event_id")]
    public Guid MessagingInboundEventId { get; set; }

    [JsonPropertyName("provider_event_id")]
    public string ProviderEventId { get; set; } = string.Empty;

    [JsonPropertyName("external_thread_id")]
    public string? ExternalThreadId { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }
}

public class MessagingDeliveryResponse
{
    [JsonPropertyName("messaging_delivery_id")]
    public Guid MessagingDeliveryId { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("external_thread_id")]
    public string? ExternalThreadId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }
}

public class MessagingChannelActivityResponse
{
    [JsonPropertyName("inbound")]
    public List<MessagingInboundEventResponse> Inbound { get; set; } = new();

    [JsonPropertyName("outbound")]
    public List<MessagingDeliveryResponse> Outbound { get; set; } = new();
}

public class MessagingIdentityLinkResponse
{
    [JsonPropertyName("messaging_identity_link_id")]
    public Guid MessagingIdentityLinkId { get; set; }

    [JsonPropertyName("external_workspace_id")]
    public string? ExternalWorkspaceId { get; set; }

    [JsonPropertyName("external_user_id")]
    public string ExternalUserId { get; set; } = string.Empty;

    [JsonPropertyName("linked_user_id")]
    public Guid LinkedUserId { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

// ─── Account linking ────────────────────────────────────────────────

// Shown on the confirm page so the user knows which external identity they're
// about to bind to their account.
public class MessagingLinkPreviewResponse
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("external_user_id")]
    public string ExternalUserId { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }
}

public class MessagingLinkConfirmResponse
{
    [JsonPropertyName("linked")]
    public bool Linked { get; set; }

    [JsonPropertyName("messaging_identity_link_id")]
    public Guid MessagingIdentityLinkId { get; set; }
}
