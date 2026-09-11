using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Admin-only update. Password change is NOT here — users change their own
// password via /api/auth/change-password. Admins wanting to reset a
// forgotten password go through a separate reset flow (future).
public class UpdateUser
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; set; }
}
