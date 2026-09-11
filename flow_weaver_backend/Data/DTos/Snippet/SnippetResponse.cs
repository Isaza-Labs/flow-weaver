using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class SnippetResponse
{
    [JsonPropertyName("snippet_id")]
    public Guid SnippetId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }

    [JsonPropertyName("output_schema")]
    public JsonElement OutputSchema { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("script_language")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = string.Empty;

    [JsonPropertyName("max_parallel")]
    public int MaxParallel { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("retry_policy")]
    public JsonElement RetryPolicy { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    // Mermaid source rendered in the snippet detail page and in the
    // workflow canvas side panel when a node referencing this snippet
    // is selected. Null for built-in types (ping, rest_call, ssh,
    // integration_action, report) whose semantics are self-evident
    // from Type; required for python_snippet and transform by
    // SnippetService validation.
    [JsonPropertyName("logic_diagram_mermaid")]
    public string? LogicDiagramMermaid { get; set; }

    // S13.6: per-snippet idempotency override consumed by the rollback
    // analyzer. Null = inherit the handler default. Allowed values:
    // "idempotent", "requires_compensation", "non_reversible".
    [JsonPropertyName("idempotency")]
    public string? Idempotency { get; set; }

    // Null reads as "the author has not said" — fine for a type whose handler measures its
    // own effect, and a step that will fail for `python_snippet` until a node declares it.
    [JsonPropertyName("changes_state")]
    public bool? ChangesState { get; set; }

    [JsonPropertyName("network_enabled")]
    public bool NetworkEnabled { get; set; }

    // How many times a step using this snippet has reached `completed`, and
    // when it last did. Zero / null means nobody has ever got this snippet to
    // finish — it is a draft, not a reusable building block, and the workflow
    // editor palette files it under "Unproven" instead of mixing it in with
    // the rest.
    //
    // Derived from step_runs at read time, not stored on the snippet: it must
    // stay true after runs happen elsewhere (scheduler, webhook, another
    // user's session) without anything having to write back to this row.
    //
    // Populated on list and single reads. Always 0 in the response to a
    // create/update — the snippet has not run as part of that request, and
    // pretending otherwise would be a lie the palette then acts on.
    [JsonPropertyName("completed_run_count")]
    public int CompletedRunCount { get; set; }

    [JsonPropertyName("last_completed_run_at")]
    public DateTime? LastCompletedRunAt { get; set; }
}
