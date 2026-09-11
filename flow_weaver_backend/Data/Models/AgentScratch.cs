using System.Text.Json;

namespace flow_weaver_backend.Models;

// S16 — Harness lesson 3: scratchpad rows so the agent's intermediate
// findings (discovered devices, candidate snippets, open questions,
// resolution attempts) survive chat turns and worker restarts. Without
// these the LLM has to re-derive context from the message history on
// every reload, which is both wasteful and error-prone.
//
// Upsert semantics: (ConversationId, Key) is unique. Tools
// write findings under stable keys ("discovered_devices",
// "candidate_snippets", "open_questions"); on the next turn the chat
// controller hydrates the system prompt with them.
public class AgentScratch : BaseModel
{
    public Guid AgentScratchId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? WorkflowPlanId { get; set; }
    public string Key { get; set; } = string.Empty;
    public JsonElement Value { get; set; } = default;

    // Optional human note attached when the agent or a user pinned the
    // scratch — surfaced in the chat UI's "context" panel.
    public string? Note { get; set; }
}
