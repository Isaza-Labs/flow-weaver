using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AuditEventResponse
{
    [JsonPropertyName("audit_event_id")]
    public Guid AuditEventId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    // Username for a signed-in request; the automation identity
    // ("git-webhook", "workflow-runner", …) when UserId is null.
    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("entity_id")]
    public Guid? EntityId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("before_json")]
    public JsonElement BeforeJson { get; set; }

    [JsonPropertyName("after_json")]
    public JsonElement AfterJson { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }
}
