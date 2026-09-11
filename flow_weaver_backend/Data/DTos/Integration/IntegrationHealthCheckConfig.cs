using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Parsed form of the integration.HealthCheck jsonb column. Used internally
// by the health check endpoint. Defaults applied by the handler: Path="/",
// ExpectedStatus=200 when missing/zero.
internal class IntegrationHealthCheckConfig
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("expected_status")]
    public int ExpectedStatus { get; set; } = 200;
}
