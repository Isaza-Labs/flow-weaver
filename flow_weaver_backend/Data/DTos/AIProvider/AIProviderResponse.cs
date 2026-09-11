using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Public response — APIKey deliberately omitted (sensitive, stored encrypted).
public class AIProviderResponse
{
    [JsonPropertyName("ai_provider_id")]
    public Guid AIProviderId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string? BaseURL { get; set; }

    [JsonPropertyName("default_model")]
    public string DefaultModel { get; set; } = string.Empty;

    [JsonPropertyName("config")]
    public JsonElement Config { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
