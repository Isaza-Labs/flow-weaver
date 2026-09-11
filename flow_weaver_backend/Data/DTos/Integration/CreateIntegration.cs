using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateIntegration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("base_url")]
    public string BaseURL { get; set; } = string.Empty;

    [JsonPropertyName("auth_config")]
    public JsonElement? AuthConfig { get; set; }

    [JsonPropertyName("headers")]
    public JsonElement? Headers { get; set; }

    [JsonPropertyName("tls_skip_verify")]
    public bool TLSSkipVerify { get; set; }

    [JsonPropertyName("allow_private_network")]
    public bool AllowPrivateNetwork { get; set; }

    [JsonPropertyName("health_check")]
    public JsonElement? HealthCheck { get; set; }
}
