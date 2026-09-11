using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Configuration for a rest_call execution. Wire format of service_config.
// Handler applies defaults: Method="GET", TimeoutSeconds=30.
internal class RestCallConfig
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 30;
}
