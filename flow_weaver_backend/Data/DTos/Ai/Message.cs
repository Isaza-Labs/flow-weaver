using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Simple chat message for the text-only chat path (no tool calling).
// Tool-calling conversations use ChatTurn instead, which carries tool_calls + tool_call_id.
// Role is one of: "system", "user", "assistant".
public class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
