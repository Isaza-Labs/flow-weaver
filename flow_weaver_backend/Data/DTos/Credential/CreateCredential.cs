using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateCredential
{
    [Required]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    // Phase 2b: optional passphrase protecting PrivateKey at rest.
    [JsonPropertyName("key_passphrase")]
    public string? KeyPassphrase { get; set; }

    // Phase 2b: which auth method the consuming handler should use.
    // Valid values: "password" (default for back-compat) or "key". The
    // service rejects "key" when PrivateKey is empty so a misconfigured
    // row can't sneak through create.
    [JsonPropertyName("auth_method")]
    public string? AuthMethod { get; set; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; set; }
}
