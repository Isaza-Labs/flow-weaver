using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class ErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}
