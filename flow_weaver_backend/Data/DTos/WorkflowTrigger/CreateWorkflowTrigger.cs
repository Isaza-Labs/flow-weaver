using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateWorkflowTrigger
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("route")]
    public string? Route { get; set; }

    [JsonPropertyName("cron_expression")]
    public string? CronExpression { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }

    [JsonPropertyName("input_defaults")]
    public JsonElement? InputDefaults { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    // Webhook triggers only: accept unsigned deliveries when no secret is set.
    // Ignored for other types. Default false (a secret is auto-generated on
    // create, so signed is the norm).
    [JsonPropertyName("allow_unsigned")]
    public bool? AllowUnsigned { get; set; }

    [JsonPropertyName("notification_webhook_url")]
    public string? NotificationWebhookURL { get; set; }

    [JsonPropertyName("notify_on")]
    public List<string>? NotifyOn { get; set; }

    [JsonPropertyName("target_devices")]
    public List<Guid>? TargetDevices { get; set; }

    // Webhook triggers only: let the delivery body pick targets via
    // `target_devices` / `target_pools`. Default false — the body is otherwise
    // ignored for targeting and the run uses `target_devices` above. Even when
    // true the body can only narrow that list, never extend it.
    [JsonPropertyName("allow_target_override")]
    public bool? AllowTargetOverride { get; set; }
}
