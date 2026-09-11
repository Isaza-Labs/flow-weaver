using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools;

namespace flow_weaver_backend.Services.Ai.Conversation;

// Transport-agnostic output channel for a single agent turn. The runner emits
// semantic events; an implementation decides how to deliver them.
// SseAgentEventSink streams them to an HTTP response (the chat UI);
// NullAgentEventSink drops them (non-streaming callers and the messaging
// worker, which only care about the returned AgentTurnResult).
//
// Implementations should be tolerant of a cancelled ct (the client may have
// disconnected mid-turn) and must never throw in a way that aborts the loop —
// a dead output channel must not lose the persisted turn.
public interface IAgentEventSink
{
    Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct);
    Task TextAsync(string delta, CancellationToken ct);
    Task ToolStartAsync(string toolName, JsonElement arguments, CancellationToken ct);
    Task ToolResultAsync(string toolName, ToolCallOutput output, CancellationToken ct);
    Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct);
    Task TimeoutAsync(string partialTail, int deadlineSeconds, CancellationToken ct);
    Task ErrorAsync(string message, string? code, CancellationToken ct);
}

// No-op sink for callers that only want the returned AgentTurnResult: the
// non-streaming /api/ai/chat endpoint and the messaging worker.
public sealed class NullAgentEventSink : IAgentEventSink
{
    public static readonly NullAgentEventSink Instance = new();

    private NullAgentEventSink()
    {
    }

    public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct) => Task.CompletedTask;
    public Task TextAsync(string delta, CancellationToken ct) => Task.CompletedTask;
    public Task ToolStartAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.CompletedTask;
    public Task ToolResultAsync(string toolName, ToolCallOutput output, CancellationToken ct) => Task.CompletedTask;
    public Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct) => Task.CompletedTask;
    public Task TimeoutAsync(string partialTail, int deadlineSeconds, CancellationToken ct) => Task.CompletedTask;
    public Task ErrorAsync(string message, string? code, CancellationToken ct) => Task.CompletedTask;
}
