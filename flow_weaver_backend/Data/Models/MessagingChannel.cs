using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

// A bidirectional messaging channel: a connection to a single provider
// workspace (a Telegram bot, a Slack app install, a WhatsApp number,
// a Teams bot). Inbound webhooks are verified against EncryptedSigningSecret;
// outbound replies authenticate with EncryptedBotToken.
//
// Identity & permissions: a turn from this channel runs as the *linked* internal
// user (see MessagingIdentityLink) with that user's real role, capped — never
// raised — by MaxRole. RequireLinkedUser gates whether an unlinked external user
// may do anything at all.
public class MessagingChannel : BaseModel
{
    public const string ProviderTelegram = "telegram";
    public const string ProviderSlack = "slack";
    public const string ProviderWhatsApp = "whatsapp";
    public const string ProviderTeams = "teams";

    public Guid MessagingChannelId { get; set; }

    // telegram | slack | whatsapp | teams. Selects the IMessagingProvider.
    public string Provider { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Outbound auth (Slack xoxb-, Telegram bot token, WhatsApp access token,
    // Teams AAD client secret). Encrypted at rest by CredentialEncryptionService.
    [JsonIgnore]
    public byte[]? EncryptedBotToken { get; set; }

    // Inbound signature/secret material (Slack signing secret, Telegram
    // secret_token, WhatsApp app secret, Teams ...). Encrypted at rest.
    // Unused for Slack Socket Mode (no HTTP webhook to verify).
    [JsonIgnore]
    public byte[]? EncryptedSigningSecret { get; set; }

    // Opt-in credential for the provider's NO-PUBLIC-INGRESS mode. Setting it on
    // an enabled channel makes the backend dial OUT and receive events over that
    // connection instead of waiting on a public webhook. Encrypted at rest.
    //
    //   slack → App-Level Token (xapp-…, scope connections:write) for Socket
    //           Mode; SlackSocketModeHostedService opens a WebSocket to Slack.
    //   teams → Azure Relay Hybrid Connection connection string (with
    //           EntityPath=…); TeamsRelayHostedService opens the Relay control
    //           channel and the Bot Framework POSTs arrive over it. Teams has no
    //           Socket Mode of its own, so the Relay stands in for one.
    //
    // Unused by telegram and whatsapp, which are webhook-only.
    [JsonIgnore]
    public byte[]? EncryptedAppToken { get; set; }

    // Non-secret provider-specific config (phone_number_id, team_id,
    // verify_token, serviceUrl base, bot username, AAD tenant/app id, …).
    public JsonElement ExternalConfig { get; set; } = default;

    // Agent that answers this channel. Null → the default assistant.
    public Guid? DefaultAgentId { get; set; }

    // Restrictive ceiling on the effective role for turns from this channel
    // (e.g. "operator"): rol_efectivo = min(linkedUser.Role, MaxRole). NEVER
    // raises privileges. Null = no extra ceiling (the user's real role applies).
    public string? MaxRole { get; set; }

    // When true (default), an external user with no verified MessagingIdentityLink
    // cannot run the agent — the bot asks them to link their account first.
    public bool RequireLinkedUser { get; set; } = true;

    // Allowlist of external user/workspace ids permitted to use the channel.
    // Empty means "nobody until explicitly allowed" is a deployment choice the
    // service layer enforces; see MessagingOptions.
    public List<string> AllowedExternalIds { get; set; } = new();

    // Escape hatch mirroring GitWebhook.AllowUnsigned: accept unverified inbound
    // deliveries (test emitters). Defaults false so the safe path is default.
    public bool AllowUnsigned { get; set; }

    public bool Enabled { get; set; }

    public DateTime? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }
}
