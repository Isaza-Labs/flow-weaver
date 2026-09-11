using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AuthEventResponse
{
    [JsonPropertyName("auth_event_id")]
    public Guid AuthEventId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTime At { get; set; }

    [JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }
}
