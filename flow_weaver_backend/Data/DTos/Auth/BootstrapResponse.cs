using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// POST /api/auth/bootstrap — development-only helper that creates a seed
// admin and returns the tokens so you can curl the API without clicking
// through a login flow. Returns the one-time admin password so you can log
// back in later; it is NOT retrievable afterwards.
public class BootstrapResponse
{
    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("initial_password")]
    public string InitialPassword { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;
}
