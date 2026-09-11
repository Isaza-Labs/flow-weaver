using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Configuration for a ping execution. Wire format of service_config for ping jobs.
// Handler applies defaults (Count=3, TimeoutSeconds=5) when values are <= 0.
internal class PingConfig
{
    [JsonPropertyName("count")]
    public int Count { get; set; } = 3;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 5;
}
