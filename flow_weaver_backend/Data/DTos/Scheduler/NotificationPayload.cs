using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// JSON body posted to a trigger's configured webhook after a scheduled run finishes.
// Intentionally flat so it pastes directly into Slack/Discord/PagerDuty webhook
// templates without nesting. Public contract — do not reshape without versioning.
public class NotificationPayload
{
    [JsonPropertyName("trigger_id")]
    public string TriggerId { get; set; } = string.Empty;

    [JsonPropertyName("trigger_name")]
    public string TriggerName { get; set; } = string.Empty;

    [JsonPropertyName("workflow_id")]
    public string WorkflowId { get; set; } = string.Empty;

    [JsonPropertyName("workflow_name")]
    public string WorkflowName { get; set; } = string.Empty;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    // RFC3339 / ISO-8601 UTC timestamp of when the trigger fired.
    [JsonPropertyName("fired_at")]
    public string FiredAt { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public string CompletedAt { get; set; } = string.Empty;
}
