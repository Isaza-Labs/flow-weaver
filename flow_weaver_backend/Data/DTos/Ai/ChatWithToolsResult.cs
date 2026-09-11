using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Single-iteration response from a tool-calling provider.
// The dispatch loop inspects StopReason to decide whether to:
//   - "tool_use"   → execute the ToolCalls and loop again
//   - "end_turn"   → return Content to the user
//   - "max_tokens" → surface truncation warning
public class ChatWithToolsResult
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("stop_reason")]
    public string StopReason { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}
