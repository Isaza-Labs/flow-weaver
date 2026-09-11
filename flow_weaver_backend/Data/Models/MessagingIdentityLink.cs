namespace flow_weaver_backend.Models;

// Maps an external messaging identity (provider user, optionally scoped to a
// workspace/team) to a real internal user. Created by the self-service account
// linking flow (MessagingLinkToken): the bot sends a one-time link, the user
// opens it authenticated, and confirms. The linked user's *current* role
// governs every turn — the link never caches the role, so a role change in
// FlowWeaver takes effect on the next message.
public class MessagingIdentityLink : BaseModel
{
    public Guid MessagingIdentityLinkId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // Slack team_id / WhatsApp business id / Telegram (null) — disambiguates the
    // same external user id across workspaces. Null when the provider has no
    // workspace concept.
    public string? ExternalWorkspaceId { get; set; }

    // The external user id (Slack U…, Telegram from.id, WhatsApp wa_id, Teams aad id).
    public string ExternalUserId { get; set; } = string.Empty;

    // The internal user whose role governs turns from this external identity.
    public Guid LinkedUserId { get; set; }

    public string? DisplayName { get; set; }
}
