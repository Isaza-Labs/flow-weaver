using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Model-facing description of one callable tool.
// Parameters is JSON Schema (draft 2020-12) — Anthropic/OpenAI/Ollama all accept it directly.
// Providers translate this into their native function/tool format at dispatch time.
public class ToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, object?> Parameters { get; set; } = new();
}
