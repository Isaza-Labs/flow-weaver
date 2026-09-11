using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Public response — AuthConfig omitted because it may contain secrets.
public class IntegrationResponse
{
    [JsonPropertyName("integration_id")]
    public Guid IntegrationId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("base_url")]
    public string BaseURL { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public JsonElement Headers { get; set; }

    [JsonPropertyName("tls_skip_verify")]
    public bool TLSSkipVerify { get; set; }

    [JsonPropertyName("allow_private_network")]
    public bool AllowPrivateNetwork { get; set; }

    [JsonPropertyName("health_check")]
    public JsonElement HealthCheck { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("last_checked_at")]
    public DateTime? LastCheckedAt { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
