using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Messaging;

// Strategy for one messaging platform: how to verify an inbound webhook, how to
// parse it into a user message, and how to send a reply. Decrypted secrets are
// passed in by the caller (ingest/worker) so providers never touch the keyring.
//
// Implementations must be stateless / thread-safe — one instance is shared
// (singleton) and resolved by Provider name.
public interface IMessagingProvider
{
    // telegram | slack | whatsapp | teams — matches MessagingChannel.Provider.
    string Provider { get; }

    // Verify the request's authenticity (signature / token / JWT) and handle any
    // provider handshake (Slack url_verification, WhatsApp hub.challenge GET).
    // decryptedSigningSecret is null when the channel has none configured.
    Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct);

    // Extract a routable user message, or null if the payload isn't one.
    InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request);

    // Push a reply to the channel. decryptedBotToken is null when the channel has
    // no outbound token configured (the provider should then fail clearly).
    Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct);
}
