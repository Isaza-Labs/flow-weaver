using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Returned by POST /auth/login and POST /auth/refresh. The access_token is
// sent as Authorization: Bearer; the refresh_token is persisted by the
// frontend (localStorage for MVP, httpOnly cookie later) and sent back on
// refresh. `expires_in` is seconds until the access_token expires, to let
// the frontend preemptively refresh ~1 min before it dies.
public class LoginResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}
