namespace flow_weaver_backend.Models;

// Auth principal. Inherits BaseModel: IsActive for soft-disable, plus
// CreatedAt/UpdatedAt timestamps. Auth-specific fields (lockout counters,
// MFA, password rotation) live here.
public class User : BaseModel
{
    public Guid UserId { get; set; }

    // Login handle. Globally unique (unique index in AppDbContext).
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // Hashed by Microsoft.AspNetCore.Identity.PasswordHasher<User>.
    // Never plaintext; never logged.
    public string PasswordHash { get; set; } = string.Empty;

    // Tracked so admins can force a password rotation and so we can
    // expire long-unchanged passwords if policy requires it.
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    // One of "admin" | "operator" | "viewer". Flat for MVP;
    // richer permissions arrive via PermissionClassifier (Sprint 4).
    public string Role { get; set; } = "viewer";

    // Lockout tracking (Sprint 1.3). Login resets to 0 on success.
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }

    // TOTP secret for future MFA (Sprint 5+). Null today.
    public string? MfaSecret { get; set; }
}
