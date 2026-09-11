using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class LoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

}
