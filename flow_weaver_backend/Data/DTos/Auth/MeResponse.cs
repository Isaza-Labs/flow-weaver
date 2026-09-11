using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Returned by GET /api/auth/me. Mirrors the JWT claims the frontend is
// already using, but served authoritatively from the DB so the UI can
// show the current role after a role change.
public class MeResponse
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("password_changed_at")]
    public DateTime PasswordChangedAt { get; set; }
}
