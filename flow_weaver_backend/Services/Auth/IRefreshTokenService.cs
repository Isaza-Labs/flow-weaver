using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Auth;

// Owns the refresh token lifecycle: generation, rotation, revocation.
// The raw token value is returned once to the caller; the DB holds only
// its SHA256 hash.
public interface IRefreshTokenService
{
    // Issues a brand-new chain (no predecessor). Returns the raw token.
    Task<(string rawToken, RefreshToken entity)> CreateAsync(
        User user, string ip, CancellationToken ct);

    // Validates + rotates. Returns null when the token is unknown, expired,
    // or already revoked (replay). On replay, invalidates the entire chain
    // for the user and returns ReplayDetected.
    Task<RotateResult> RotateAsync(string rawToken, string ip, CancellationToken ct);

    Task RevokeAsync(string rawToken, CancellationToken ct);

    // Revokes every active refresh token for the user (the entire chain).
    // Called on password change, account disable, or replay detection so a
    // compromised session cannot keep renewing after the credential rotates.
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct);
}

public record RotateResult(
    RotateStatus Status,
    string? NewRawToken,
    RefreshToken? NewEntity,
    User? User)
{
    public static RotateResult Unknown() => new(RotateStatus.Unknown, null, null, null);
    public static RotateResult Expired() => new(RotateStatus.Expired, null, null, null);
    public static RotateResult ReplayDetected() => new(RotateStatus.ReplayDetected, null, null, null);
    public static RotateResult Ok(string token, RefreshToken entity, User user)
        => new(RotateStatus.Ok, token, entity, user);
}

public enum RotateStatus
{
    Ok,
    Unknown,
    Expired,
    ReplayDetected,
}
