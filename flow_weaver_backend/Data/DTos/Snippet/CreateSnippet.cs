using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateSnippet
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("output_schema")]
    public JsonElement? OutputSchema { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("script_language")]
    public string? ScriptLanguage { get; set; }

    [JsonPropertyName("target_mode")]
    public string TargetMode { get; set; } = string.Empty;

    [JsonPropertyName("max_parallel")]
    public int? MaxParallel { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("verified")]
    public bool? Verified { get; set; }

    [JsonPropertyName("retry_policy")]
    public JsonElement? RetryPolicy { get; set; }

    // Mermaid source that documents the task's internal logic. Required
    // for python_snippet and transform (the types whose behavior is
    // opaque from the outside). Optional for built-in handler types
    // whose semantics are self-describing from Type.
    [JsonPropertyName("logic_diagram_mermaid")]
    public string? LogicDiagramMermaid { get; set; }

    // Optional override; null = inherit handler default. Allowed:
    // "idempotent" | "requires_compensation" | "non_reversible".
    // Validated by the snippet service against the handler's floor —
    // the override may not weaken below the handler's DefaultIdempotency.
    [JsonPropertyName("idempotency")]
    public string? Idempotency { get; set; }

    // Whether a step running this snippet CHANGES anything — a different question from
    // idempotency, which says whether an action could be UNDONE. The handler answers it
    // where it can see the action; where the author supplies the action instead (a python
    // script, a playbook) only the author can, and this is where they say so.
    //
    // For a node that carries the action rather than the snippet (ssh commands, an mcp tool)
    // the node's `config_overrides.changes` says it, and wins over this.
    //
    // Null means the author has not said. That is correct for a type whose handler measures
    // its own effect, and a step that will FAIL for one whose handler cannot.
    [JsonPropertyName("changes_state")]
    public bool? ChangesState { get; set; }

    // Opt-in for python_snippet ONLY: run in the relaxed (network-enabled)
    // sandbox so the script can use netmiko/paramiko for interactive SSH
    // (e.g. a password change that prompts for confirmation). Setting it true
    // requires the admin role — enforced by SnippetService.
    [JsonPropertyName("network_enabled")]
    public bool? NetworkEnabled { get; set; }
}
