using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Optional body of POST /api/auth/bootstrap. The deploy/setup wizard sends
// the admin credentials the operator chose; an empty/missing body keeps the
// legacy behavior (username "admin", generated one-time password).
public class BootstrapRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    // Minimum 8 characters when supplied; omitted → a strong password is
    // generated and returned once in BootstrapResponse.initial_password.
    [JsonPropertyName("password")]
    public string? Password { get; set; }
}
