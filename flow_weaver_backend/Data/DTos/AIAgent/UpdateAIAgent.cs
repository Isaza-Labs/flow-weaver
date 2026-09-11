using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateAIAgent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("provider_id")]
    public Guid? ProviderId { get; set; }

    [JsonPropertyName("model_override")]
    public string? ModelOverride { get; set; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    [JsonPropertyName("max_iterations")]
    public int? MaxIterations { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
