using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Structured result of a rest_call HTTP request. Serialized into step_run output.
internal class RestCallOutput
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();
}
