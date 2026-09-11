using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Response shape for GET /integrationaction/actions/all — the cross-integration
// list used by the workflow editor service palette. Leaner than
// IntegrationActionResponse: omits response_schema / enabled / created_at and
// hardcodes service_type.
public class IntegrationActionPaletteItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("integration_id")]
    public Guid IntegrationId { get; set; }

    [JsonPropertyName("integration_name")]
    public string IntegrationName { get; set; } = string.Empty;

    [JsonPropertyName("integration_type")]
    public string IntegrationType { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("path_params")]
    public JsonElement PathParams { get; set; }

    [JsonPropertyName("query_params")]
    public JsonElement QueryParams { get; set; }

    [JsonPropertyName("request_body")]
    public JsonElement RequestBody { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    // Always "integration_action" — kept on the DTO so the workflow editor
    // can dispatch to the right handler.
    [JsonPropertyName("service_type")]
    public string ServiceType { get; set; } = "integration_action";
}
