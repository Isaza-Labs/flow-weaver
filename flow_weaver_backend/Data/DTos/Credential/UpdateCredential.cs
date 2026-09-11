using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateCredential
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    [JsonPropertyName("key_passphrase")]
    public string? KeyPassphrase { get; set; }

    [JsonPropertyName("auth_method")]
    public string? AuthMethod { get; set; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; set; }
}
