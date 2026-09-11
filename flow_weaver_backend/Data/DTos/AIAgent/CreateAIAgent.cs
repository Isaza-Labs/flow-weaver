using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateAIAgent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("provider_id")]
    public Guid? ProviderId { get; set; }

    [JsonPropertyName("model_override")]
    public string? ModelOverride { get; set; }

    [JsonPropertyName("system_prompt")]
    public string SystemPrompt { get; set; } = string.Empty;

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }

    [JsonPropertyName("max_iterations")]
    public int? MaxIterations { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; set; }
}
