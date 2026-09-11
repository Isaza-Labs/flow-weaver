using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// PasswordHash, FailedLoginCount, LockedUntil, MfaSecret are NEVER in the
// response — they are admin-only DB state. /api/users exposes only what
// an admin needs for user management UI.
public class UserResponse
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("password_changed_at")]
    public DateTime PasswordChangedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
