using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateSnippet
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

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
    public string? TargetMode { get; set; }

    [JsonPropertyName("max_parallel")]
    public int? MaxParallel { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("verified")]
    public bool? Verified { get; set; }

    [JsonPropertyName("retry_policy")]
    public JsonElement? RetryPolicy { get; set; }

    [JsonPropertyName("logic_diagram_mermaid")]
    public string? LogicDiagramMermaid { get; set; }

    // See CreateSnippet for semantics. PATCH-style: omit to keep,
    // pass null to clear the override and fall back to handler default,
    // pass one of the three values to set.
    [JsonPropertyName("idempotency")]
    public string? Idempotency { get; set; }

    // See CreateSnippet for semantics. PATCH-style: omit to keep what is there.
    [JsonPropertyName("changes_state")]
    public bool? ChangesState { get; set; }

    // python_snippet only; setting true requires admin (SnippetService).
    [JsonPropertyName("network_enabled")]
    public bool? NetworkEnabled { get; set; }

    // python_snippet / ansible_playbook only. PATCH-style: omit (null) to
    // keep the current environment; the empty GUID
    // ("00000000-0000-0000-0000-000000000000") clears it back to the image's
    // system python3; any other id selects that environment.
    [JsonPropertyName("python_environment_id")]
    public Guid? PythonEnvironmentId { get; set; }
}
