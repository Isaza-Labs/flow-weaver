using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateIntegration
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseURL { get; set; }

    [JsonPropertyName("auth_config")]
    public JsonElement? AuthConfig { get; set; }

    [JsonPropertyName("headers")]
    public JsonElement? Headers { get; set; }

    [JsonPropertyName("tls_skip_verify")]
    public bool? TLSSkipVerify { get; set; }

    [JsonPropertyName("allow_private_network")]
    public bool? AllowPrivateNetwork { get; set; }

    // S13.3: optional justification text written into the audit row when
    // AllowPrivateNetwork transitions. The backend ignores the value
    // unless the flag actually changed; the UI prompts for it whenever
    // the toggle moves so reviewers see *why* the SSRF guard is relaxed
    // alongside the *who* and *when*.
    [JsonPropertyName("allow_private_network_reason")]
    public string? AllowPrivateNetworkReason { get; set; }

    [JsonPropertyName("health_check")]
    public JsonElement? HealthCheck { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
