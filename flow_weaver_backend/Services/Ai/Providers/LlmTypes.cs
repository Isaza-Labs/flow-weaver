using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Providers;

public sealed class ChatResult
{
    public required string Content { get; init; }
    public List<ToolCallResult>? ToolCalls { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public string? StopReason { get; init; }
}

public sealed class ToolCallResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
}

public sealed class ChatStreamEvent
{
    public required string Type { get; init; } // "text_delta", "tool_call", "done", "error"
    public string? TextDelta { get; init; }
    public ToolCallResult? ToolCall { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public string? Error { get; init; }
    // Why the model stopped, carried on "done" events when the provider said:
    // OpenAI "stop"/"tool_calls"/"length", Anthropic "end_turn"/"tool_use"/
    // "max_tokens", Gemini "STOP"/"MAX_TOKENS", Ollama "stop"/"length". Null when
    // the stream ended without saying. The runner acts on the out-of-tokens ones:
    // they mean the answer is not finished, and nothing in the text shows that.
    public string? StopReason { get; init; }
}

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement ParametersSchema { get; init; }
}

public sealed class LlmMessage
{
    public required string Role { get; init; } // "system", "user", "assistant", "tool"
    public string? Content { get; init; }
    public List<ToolCallResult>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}
