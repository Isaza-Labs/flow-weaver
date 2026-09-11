using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class WorkflowResponse
{
    [JsonPropertyName("workflow_id")]
    public Guid WorkflowId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "v1";

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }

    [JsonPropertyName("nodes")]
    public JsonElement Nodes { get; set; }

    [JsonPropertyName("edges")]
    public JsonElement Edges { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("promoted_from")]
    public Guid? PromotedFrom { get; set; }

    [JsonPropertyName("change_summary")]
    public string ChangeSummary { get; set; } = string.Empty;

    [JsonPropertyName("promoted_at")]
    public DateTime? PromotedAt { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    // Non-blocking advisories from the reference validator. Populated on
    // create/update when the DAG looks suspicious (workflow description
    // mentions "email" but no integration_action node, etc.). The row is
    // already persisted — the field exists so the agent / UI can show
    // the warning and offer a one-click fix instead of waiting for the
    // user to run a broken workflow.
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; set; }

    // Bundle import only (POST /workflow/import with a .bundle.json). Every
    // degradation and every thing the operator still has to configure —
    // secrets that could not be verified, triggers created disabled, a
    // snippet that lost network_enabled. Silence means nothing was degraded
    // (bundle/SPEC.md §7). Null on every other path.
    [JsonPropertyName("import_notes")]
    public IReadOnlyList<string>? ImportNotes { get; set; }

    // What the bundle import created beside the workflow itself, so the UI
    // can say so and the operator can find them.
    [JsonPropertyName("created_snippets")]
    public IReadOnlyList<CreatedRef>? CreatedSnippets { get; set; }

    [JsonPropertyName("created_workflows")]
    public IReadOnlyList<CreatedRef>? CreatedWorkflows { get; set; }

    [JsonPropertyName("created_triggers")]
    public IReadOnlyList<CreatedRef>? CreatedTriggers { get; set; }
}

// Identity of something an import created: enough to link to it, no more.
public sealed record CreatedRef(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);
