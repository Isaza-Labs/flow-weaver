using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Conversation;

// One user message to run through the agent. UserId is NOT carried here: it
// comes from the scoped ICurrentUser the caller binds (JWT on the web,
// MutableCurrentUser.Bind in the messaging worker). The tool layer reads that
// same ICurrentUser for RBAC, so identity has a single source per scope.
public sealed class AgentTurnRequest
{
    public string Message { get; init; } = string.Empty;
    public Guid? AgentId { get; init; }
    public Guid? ConversationId { get; init; }

    // Wall-clock budget for the turn. Null → AiChatOptions.StreamDeadlineSeconds.
    public int? DeadlineSecondsOverride { get; init; }
}

public sealed record AgentToolCall(string Name, JsonElement Arguments, bool Success);

// The full outcome of a turn, returned regardless of the sink. The messaging
// worker (F2) uses FinalText to reply on the external channel; the non-streaming
// HTTP endpoint maps this to its JSON body.
public sealed class AgentTurnResult
{
    public required Guid ConversationId { get; init; }
    public required bool IsNewConversation { get; init; }
    public string FinalText { get; init; } = string.Empty;
    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = Array.Empty<AgentToolCall>();
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public int Iterations { get; init; }
    public bool TimedOut { get; init; }
    // The model stopped for lack of output tokens, not because it was finished.
    // FinalText already ends with the notice the user saw.
    public bool OutputCutOff { get; init; }
    public string? Error { get; init; }
}
