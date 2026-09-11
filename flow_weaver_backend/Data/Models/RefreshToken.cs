namespace flow_weaver_backend.Models;

// Short-lived rotation token that buys a fresh access token.
// We persist only the SHA256 of the raw token so a DB leak doesn't
// hand an attacker active sessions.
//
// Rotation: on every use, the current token is marked RevokedAt + the
// new token's hash is stored in ReplacedByTokenHash. If a token with
// RevokedAt != null is ever presented again, we treat it as replay
// and invalidate the entire chain for that user.
public class RefreshToken
{
    public Guid RefreshTokenId { get; set; }
    public Guid UserId { get; set; }

    // SHA256(raw_token) as a 64-char hex. The raw token is never stored.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    // Null while the token is live. Set on logout, rotation, or revoke-chain.
    public DateTime? RevokedAt { get; set; }

    // When this token was rotated, points to the hash of its replacement.
    // Used by the chain-replay detector.
    public string? ReplacedByTokenHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedFromIp { get; set; } = string.Empty;
}
