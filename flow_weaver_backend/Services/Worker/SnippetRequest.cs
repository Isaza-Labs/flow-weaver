using System.Text.Json;

namespace flow_weaver_backend.Services.Worker;

// Immutable snapshot of everything a handler needs to execute one step.
// Built by the worker from the step_run + snippet rows.
public sealed class SnippetRequest
{
    public required Guid StepRunId { get; init; }
    public required Guid WorkflowRunId { get; init; }
    public required string NodeId { get; init; }
    public required Guid? SnippetId { get; init; }
    public required string SnippetType { get; init; }
    public required JsonElement InputPayload { get; init; }
    public required Guid? DeviceId { get; init; }
    // Snapshot of the matching Snippet fields. Handlers that carry
    // their "code" on the snippet row (python snippets, ansible playbooks,
    // transform expressions) read from here instead of expecting every
    // per-step InputPayload to repeat it. Null when no snippet
    // is associated (sentinel / subflow nodes).
    public string? SnippetCode { get; init; }
    public string? ScriptLanguage { get; init; }
    public int SnippetTimeoutSeconds { get; init; }
    public string? TargetMode { get; init; }

    // python_snippet opt-in: run in the relaxed (network-enabled) sandbox with
    // the extended import allowlist. Mirrors Snippet.NetworkEnabled.
    public bool NetworkEnabled { get; init; }
}
