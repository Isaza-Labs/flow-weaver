using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos.Messaging;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Messaging;

// Self-service account linking. CreateLinkAsync mints a one-time token (called by
// the ingest path when an unlinked user messages the bot); Preview/Confirm are
// the authenticated endpoints the user hits from the emailed/DM'd link. Confirm
// binds the external identity to the *authenticated* user and consumes the
// token atomically, so a token can never be replayed.
public interface IMessagingLinkService
{
    Task<LinkInvite> CreateLinkAsync(
        MessagingChannel channel, string? externalWorkspaceId, string externalUserId, CancellationToken ct);
    Task<ActionResult<MessagingLinkPreviewResponse>> PreviewAsync(string token);
    Task<ActionResult<MessagingLinkConfirmResponse>> ConfirmAsync(string token);
}

public sealed record LinkInvite(string Cleartext, string Url);

public class MessagingLinkService : IMessagingLinkService
{
    private readonly IMessagingLinkTokenRepository _tokens;
    private readonly IMessagingChannelRepository _channels;
    private readonly IMessagingIdentityLinkRepository _links;
    private readonly ICurrentUser _caller;
    private readonly MessagingOptions _options;

    public MessagingLinkService(
        IMessagingLinkTokenRepository tokens,
        IMessagingChannelRepository channels,
        IMessagingIdentityLinkRepository links,
        ICurrentUser caller,
        IOptions<MessagingOptions> options)
    {
        _tokens = tokens;
        _channels = channels;
        _links = links;
        _caller = caller;
        _options = options.Value;
    }

    public async Task<LinkInvite> CreateLinkAsync(
        MessagingChannel channel, string? externalWorkspaceId, string externalUserId, CancellationToken ct)
    {
        var cleartext = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var ttl = _options.LinkTokenTtlMinutes <= 0 ? 15 : _options.LinkTokenTtlMinutes;
        var token = new MessagingLinkToken
        {
            MessagingLinkTokenId = Guid.NewGuid(),
            MessagingChannelId = channel.MessagingChannelId,
            ExternalWorkspaceId = externalWorkspaceId ?? string.Empty,
            ExternalUserId = externalUserId,
            TokenHash = HashToken(cleartext),
            ExpiresAt = now.AddMinutes(ttl),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _tokens.Add(token);
        await _tokens.SaveChangesAsync(ct);

        // Without a configured public base URL we can only build a relative
        // "/link?token=..." the user can't open. Return an empty Url so the
        // caller degrades to a "link from the web app" message instead of DMing
        // a broken link. Set Messaging:PublicBaseUrl (env
        // MESSAGING_PUBLIC_BASE_URL) to the user-facing frontend URL to fix.
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var url = string.IsNullOrEmpty(baseUrl)
            ? string.Empty
            : $"{baseUrl}/link?token={cleartext}";
        return new LinkInvite(cleartext, url);
    }

    public async Task<ActionResult<MessagingLinkPreviewResponse>> PreviewAsync(string token)
    {
        var (tok, channel) = await ResolveForCurrentUserAsync(token);
        return new MessagingLinkPreviewResponse
        {
            Provider = channel.Provider,
            ChannelName = channel.Name,
            ExternalUserId = tok.ExternalUserId,
            ExpiresAt = tok.ExpiresAt,
        };
    }

    public async Task<ActionResult<MessagingLinkConfirmResponse>> ConfirmAsync(string token)
    {
        var (tok, _) = await ResolveForCurrentUserAsync(token);
        var now = DateTime.UtcNow;

        // Single-use: atomically consume the token BEFORE creating the link, so
        // two concurrent confirms of the same token can't both proceed. The
        // loser sees the token as already gone.
        if (!await _tokens.TryConsumeAsync(tok.MessagingLinkTokenId, _caller.UserId, now))
            throw new NotFoundException("link token invalid or expired");

        var workspace = tok.ExternalWorkspaceId ?? string.Empty;

        // Re-link if the external identity already maps to someone; otherwise
        // create. Keeps the unique (channel, workspace, user) index happy.
        var existing = await _links.FindByExternalAsync(tok.MessagingChannelId, workspace, tok.ExternalUserId);

        Guid linkId;
        if (existing is not null)
        {
            var tracked = await _links.FindTrackedByIdAsync(existing.MessagingIdentityLinkId);
            tracked!.LinkedUserId = _caller.UserId;
            tracked.UpdatedAt = now;
            linkId = tracked.MessagingIdentityLinkId;
        }
        else
        {
            var link = new MessagingIdentityLink
            {
                MessagingIdentityLinkId = Guid.NewGuid(),
                MessagingChannelId = tok.MessagingChannelId,
                ExternalWorkspaceId = workspace,
                ExternalUserId = tok.ExternalUserId,
                LinkedUserId = _caller.UserId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _links.Add(link);
            linkId = link.MessagingIdentityLinkId;
        }

        // Token already consumed atomically above; just persist the link.
        await _links.SaveChangesAsync();

        return new MessagingLinkConfirmResponse { Linked = true, MessagingIdentityLinkId = linkId };
    }

    // Resolves an active token by hash. A wrong or expired token looks
    // identical to a missing one (404) so we don't leak which tokens exist.
    private async Task<(MessagingLinkToken token, MessagingChannel channel)> ResolveForCurrentUserAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new NotFoundException("link token invalid or expired");

        var tok = await _tokens.FindActiveByHashAsync(HashToken(token), DateTime.UtcNow);
        if (tok is null)
            throw new NotFoundException("link token invalid or expired");

        var channel = await _channels.GetByIdAsync(tok.MessagingChannelId, tracking: false)
            ?? throw new NotFoundException("messaging channel", tok.MessagingChannelId);

        return (tok, channel);
    }

    private static byte[] HashToken(string cleartext) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(cleartext));
}
