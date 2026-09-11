using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateWorkflowTrigger
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

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
    [JsonPropertyName("allow_unsigned")]
    public bool? AllowUnsigned { get; set; }

    [JsonPropertyName("notification_webhook_url")]
    public string? NotificationWebhookURL { get; set; }

    [JsonPropertyName("notify_on")]
    public List<string>? NotifyOn { get; set; }

    [JsonPropertyName("target_devices")]
    public List<Guid>? TargetDevices { get; set; }

    // Webhook triggers only: let the delivery body pick targets. Widening the
    // blast radius of an anonymous, secret-authenticated endpoint, so it is
    // audited like enabled / allow_unsigned.
    [JsonPropertyName("allow_target_override")]
    public bool? AllowTargetOverride { get; set; }
}
