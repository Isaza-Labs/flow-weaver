using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateIntegrationAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("path_params")]
    public JsonElement? PathParams { get; set; }

    [JsonPropertyName("query_params")]
    public JsonElement? QueryParams { get; set; }

    [JsonPropertyName("request_body")]
    public JsonElement? RequestBody { get; set; }

    [JsonPropertyName("response_schema")]
    public JsonElement? ResponseSchema { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}
