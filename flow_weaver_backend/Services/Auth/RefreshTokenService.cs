using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Services.Auth;

public class RefreshTokenService : IRefreshTokenService
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly IUserRepository _users;
    private readonly JwtOptions _options;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        IRefreshTokenRepository tokens,
        IUserRepository users,
        IOptions<JwtOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        _tokens = tokens;
        _users = users;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string rawToken, RefreshToken entity)> CreateAsync(
        User user, string ip, CancellationToken ct)
    {
        var raw = GenerateRawToken();
        var entity = new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(_options.RefreshTokenDays),
            CreatedAt = DateTime.UtcNow,
            CreatedFromIp = ip,
        };

        _tokens.Add(entity);
        await _tokens.SaveChangesAsync(ct);

        // Log the token_id and a short prefix of the hash — never the raw
        // token. Prefix is just enough to correlate with the `token_hash`
        // column in the DB without exposing a reversible secret.
        _logger.LogDebug(
            "auth.refresh_token.issued token_id={TokenId} user_id={UserId} hash_prefix={HashPrefix} expires_at={ExpiresAt}",
            entity.RefreshTokenId, user.UserId,
            entity.TokenHash[..Math.Min(8, entity.TokenHash.Length)],
            entity.ExpiresAt);

        return (raw, entity);
    }

    public async Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            _logger.LogWarning("auth.refresh_token.not_found reason=empty_token");
            return RotateResult.Unknown();
        }

        var hash = Hash(rawToken);
        var existing = await _tokens.FindByHashAsync(hash, ct);

        if (existing is null)
        {
            _logger.LogWarning(
                "auth.refresh_token.not_found hash_prefix={HashPrefix}",
                hash[..Math.Min(8, hash.Length)]);
            return RotateResult.Unknown();
        }

        // REPLAY: a revoked token should never be presented again. If it is,
        // the attacker got hold of it — invalidate every live token in the
        // same user's chain as a panic-button move.
        if (existing.RevokedAt is not null)
        {
            await RevokeChainAsync(existing.UserId, ct);
            _logger.LogWarning(
                "auth.refresh_token.reuse_detected token_id={TokenId} user_id={UserId} ip={Ip}",
                existing.RefreshTokenId, existing.UserId, ip);
            return RotateResult.ReplayDetected();
        }

        if (existing.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "auth.refresh_token.expired token_id={TokenId} user_id={UserId} expired_at={ExpiredAt}",
                existing.RefreshTokenId, existing.UserId, existing.ExpiresAt);
            return RotateResult.Expired();
        }

        var user = await _users.FindActiveByIdAsync(existing.UserId, ct);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning(
                "auth.refresh_token.not_found reason=user_inactive token_id={TokenId} user_id={UserId}",
                existing.RefreshTokenId, existing.UserId);
            return RotateResult.Unknown();
        }

        // Issue new + mark old as rotated. Done inside one SaveChangesAsync
        // so the chain stays consistent even under races.
        var newRaw = GenerateRawToken();
        var newEntity = new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = Hash(newRaw),
            ExpiresAt = DateTime.UtcNow.AddDays(_options.RefreshTokenDays),
            CreatedAt = DateTime.UtcNow,
            CreatedFromIp = ip,
        };

        existing.RevokedAt = DateTime.UtcNow;
        existing.ReplacedByTokenHash = newEntity.TokenHash;

        _tokens.Add(newEntity);
        await _tokens.SaveChangesAsync(ct);

        _logger.LogDebug(
            "auth.refresh_token.rotate.ok old_token_id={OldTokenId} new_token_id={NewTokenId} user_id={UserId}",
            existing.RefreshTokenId, newEntity.RefreshTokenId, user.UserId);

        return RotateResult.Ok(newRaw, newEntity, user);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;

        var hash = Hash(rawToken);
        var existing = await _tokens.FindActiveByHashAsync(hash, ct);
        if (existing is null)
        {
            _logger.LogWarning(
                "auth.refresh_token.not_found reason=revoke_miss hash_prefix={HashPrefix}",
                hash[..Math.Min(8, hash.Length)]);
            return;
        }

        existing.RevokedAt = DateTime.UtcNow;
        await _tokens.SaveChangesAsync(ct);

        _logger.LogInformation(
            "auth.refresh_token.revoked token_id={TokenId} user_id={UserId}",
            existing.RefreshTokenId, existing.UserId);
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
        => RevokeChainAsync(userId, ct);

    // Crypto-strong 256-bit token, base64url-encoded. Length: 43 chars.
    private static string GenerateRawToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes); // 64 chars uppercase
    }

    private async Task RevokeChainAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var live = await _tokens.GetActiveByUserAsync(userId, ct);
        foreach (var t in live) t.RevokedAt = now;
        await _tokens.SaveChangesAsync(ct);

        if (live.Count > 0)
        {
            _logger.LogInformation(
                "auth.refresh_token.revoked reason=chain user_id={UserId} revoked_count={RevokedCount}",
                userId, live.Count);
        }
    }
}
