using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Extends IntegrationActionResponse with parent integration metadata
// for the cross-integration action listing endpoint.
public class IntegrationActionWithIntegration : IntegrationActionResponse
{
    [JsonPropertyName("integration_name")]
    public string IntegrationName { get; set; } = string.Empty;

    [JsonPropertyName("integration_type")]
    public string IntegrationType { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseURL { get; set; } = string.Empty;
}
