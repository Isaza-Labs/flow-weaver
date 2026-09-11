using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class WorkflowTriggerResponse
{
    [JsonPropertyName("workflow_trigger_id")]
    public Guid WorkflowTriggerId { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid WorkflowId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    // Relative path a webhook-type trigger is fired at (POST it, signed). Null
    // for non-webhook triggers. The UI prefixes it with the app origin.
    [JsonPropertyName("webhook_path")]
    public string? WebhookPath { get; set; }

    // Whether a signing secret is configured. The secret itself is never
    // returned except once, right after create/rotate (see WebhookSecret).
    [JsonPropertyName("has_webhook_secret")]
    public bool HasWebhookSecret { get; set; }

    // Accept unauthenticated deliveries when no secret is set (admin opt-in).
    [JsonPropertyName("allow_unsigned")]
    public bool AllowUnsigned { get; set; }

    // Let the delivery body choose targets (webhook only, admin opt-in).
    [JsonPropertyName("allow_target_override")]
    public bool AllowTargetOverride { get; set; }

    // Plaintext signing secret — populated ONLY in the immediate response to a
    // create (webhook type) or rotate, never on reads. Omitted from JSON when
    // null so it can't leak on a normal GET.
    [JsonPropertyName("webhook_secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebhookSecret { get; set; }

    [JsonPropertyName("cron_expression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = string.Empty;

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }

    [JsonPropertyName("input_defaults")]
    public JsonElement InputDefaults { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("next_run_at")]
    public DateTime? NextRunAt { get; set; }

    [JsonPropertyName("last_run_at")]
    public DateTime? LastRunAt { get; set; }

    [JsonPropertyName("last_run_status")]
    public string? LastRunStatus { get; set; }

    [JsonPropertyName("notification_webhook_url")]
    public string? NotificationWebhookURL { get; set; }

    [JsonPropertyName("notify_on")]
    public List<string> NotifyOn { get; set; } = new();

    [JsonPropertyName("target_devices")]
    public List<Guid> TargetDevices { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
