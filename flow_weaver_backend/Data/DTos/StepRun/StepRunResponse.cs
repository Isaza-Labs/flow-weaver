using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class StepRunResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("workflow_run_id")]
    public Guid WorkflowRunId { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("snippet_id")]
    public Guid? SnippetId { get; set; }

    [JsonPropertyName("device_id")]
    public Guid? DeviceId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("input_payload")]
    public JsonElement InputPayload { get; set; }

    [JsonPropertyName("output_payload")]
    public JsonElement OutputPayload { get; set; }

    [JsonPropertyName("logs")]
    public string Logs { get; set; } = string.Empty;

    // The machine-readable half of a failure (`unresolved_template`,
    // `integration_not_authored`, …). Persisted since the engine's first
    // version but never exposed, so the codes the troubleshooting docs index on
    // were invisible in the UI.
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("worker_id")]
    public string? WorkerId { get; set; }

    /// <summary>
    /// Whether this step CHANGED anything, which is a different question from whether it
    /// succeeded. Null for steps recorded before the change signal existed.
    /// </summary>
    [JsonPropertyName("changed")]
    public bool? Changed { get; set; }
}
