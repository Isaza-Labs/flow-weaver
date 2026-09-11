namespace flow_weaver_backend.Services.Ai.Conversation;

// Runs exactly one agent turn (resolve agent + LLM tool-calling loop + tool
// dispatch + persistence) for the caller bound on the current scope. Transport
// lives entirely in the sink, so the same loop serves the web chat (SSE) and
// the messaging worker (no sink). RunAsync never throws for an agent-level
// failure — it reports it on the sink and in AgentTurnResult.Error — so an SSE
// caller that already committed headers still gets a structured event.
public interface IAgentConversationRunner
{
    Task<AgentTurnResult> RunAsync(
        AgentTurnRequest request, IAgentEventSink sink, CancellationToken ct);
}
