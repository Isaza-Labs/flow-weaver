using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateAIProvider
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseURL { get; set; }

    [JsonPropertyName("api_key")]
    public string? APIKey { get; set; }

    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
