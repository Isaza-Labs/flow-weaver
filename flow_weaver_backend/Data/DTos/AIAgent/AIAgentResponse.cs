using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AIAgentResponse
{
    [JsonPropertyName("ai_agent_id")]
    public Guid AIAgentId { get; set; }

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
    public List<string> Tools { get; set; } = new();

    [JsonPropertyName("max_iterations")]
    public int MaxIterations { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
