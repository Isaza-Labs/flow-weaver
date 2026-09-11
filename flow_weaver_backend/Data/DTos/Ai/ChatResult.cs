using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Response from a non-tool-calling provider call (plain text chat).
// Tool-calling results use ChatWithToolsResult instead.
public class ChatResult
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}
