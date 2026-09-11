using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Admin-only create-user body. The role is validated against the allowed
// set. Password flows through the policy validator before hashing.
public class CreateUser
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    // "admin" | "operator" | "viewer". Defaults to viewer if omitted.
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}
