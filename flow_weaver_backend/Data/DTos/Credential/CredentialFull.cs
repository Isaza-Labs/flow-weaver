using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Internal use only — includes decrypted sensitive fields for credential injection.
// NEVER expose this DTO through public API endpoints or log its contents.
public class CredentialFull
{
    [JsonPropertyName("credential_id")]
    public Guid CredentialId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("private_key")]
    public string PrivateKey { get; set; } = string.Empty;

    [JsonPropertyName("extra")]
    public Dictionary<string, object?> Extra { get; set; } = new();

    public override string ToString() => $"CredentialFull(CredentialId={CredentialId}, Name={Name}, Type={Type})";
}
