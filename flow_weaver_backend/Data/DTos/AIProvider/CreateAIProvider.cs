using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateAIProvider
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string? BaseURL { get; set; }

    // API key arrives plaintext and must be encrypted by the service before persisting.
    [JsonPropertyName("api_key")]
    public string? APIKey { get; set; }

    [JsonPropertyName("default_model")]
    public string DefaultModel { get; set; } = string.Empty;

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }
}
