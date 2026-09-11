using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// External-identity → internal-user mappings for a channel. Resolved on every
// inbound turn; created by the account-linking confirm endpoint; listed and
// revoked from the admin UI.
public interface IMessagingIdentityLinkRepository : IRepository<MessagingIdentityLink>
{
    // Resolve the linked internal user for an external identity. workspace is
    // coalesced to "" by the caller when the provider has no workspace concept,
    // matching the unique index. Read-only.
    Task<MessagingIdentityLink?> FindByExternalAsync(
        Guid channelId, string externalWorkspaceId, string externalUserId,
        CancellationToken ct = default);

    // All links for a channel (admin view), newest first.
    Task<IReadOnlyList<MessagingIdentityLink>> ListByChannelAsync(
        Guid channelId, CancellationToken ct = default);

    // Tracked link by id so the admin can revoke (soft-delete).
    Task<MessagingIdentityLink?> FindTrackedByIdAsync(
        Guid linkId, CancellationToken ct = default);
}
