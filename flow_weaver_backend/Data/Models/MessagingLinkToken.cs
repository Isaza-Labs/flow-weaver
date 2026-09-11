namespace flow_weaver_backend.Models;

// One-time account-linking token. Minted when an unlinked external user messages
// the bot; the cleartext travels only in the link the bot DMs them. On confirm,
// the authenticated user's id is written to ConsumedByUserId and a
// MessagingIdentityLink is created. Short-lived + single-use to keep the link
// from being replayed or shared.
public class MessagingLinkToken : BaseModel
{
    public Guid MessagingLinkTokenId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // The external identity that will be linked when this token is confirmed.
    public string? ExternalWorkspaceId { get; set; }
    public string ExternalUserId { get; set; } = string.Empty;

    // Hash of the cleartext token (never store the cleartext). Lookups hash the
    // presented value and compare.
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();

    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public Guid? ConsumedByUserId { get; set; }
}
