using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Public credential response. NEVER includes password / private key — use
// CredentialFull only inside the engine / worker when injecting secrets.
public class CredentialResponse
{
    [JsonPropertyName("credential_id")]
    public Guid CredentialId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    // Phase 2b: which auth method this credential is configured for.
    // Response-only field so admins/UI can tell key-auth rows apart from
    // password ones without surfacing the secret bytes themselves.
    [JsonPropertyName("auth_method")]
    public string AuthMethod { get; set; } = "password";

    // True when the row has a non-empty private key stored. Lets the
    // UI render "(key uploaded)" without leaking any bytes.
    [JsonPropertyName("has_private_key")]
    public bool HasPrivateKey { get; set; }

    [JsonPropertyName("extra")]
    public JsonElement Extra { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
