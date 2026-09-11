using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateIntegrationAction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

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

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
