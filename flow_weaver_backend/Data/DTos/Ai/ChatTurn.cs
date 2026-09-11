using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// One turn in a tool-calling conversation. Supersedes Message for the v2 chat path.
//
// Role semantics:
//   - "system"    → system prompt
//   - "user"      → user-facing input
//   - "assistant" → model output. May carry Content (text) and/or ToolCalls
//   - "tool"      → result of a tool call. Content is the tool result as JSON;
//                   ToolCallId matches the assistant's ToolCalls[].Id that requested it.
public class ChatTurn
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ToolCallId { get; set; } = string.Empty;

    // Populated when Role = "tool"; mirrors the invoked tool name for traceability.
    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ToolName { get; set; } = string.Empty;
}
