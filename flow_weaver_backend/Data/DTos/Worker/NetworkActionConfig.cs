using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Parsed configuration for a network library action execution.
// Wire format of the service_config for network_library jobs.
internal class NetworkActionConfig
{
    // Network automation library: netmiko, napalm, scrapli, nornir, ansible
    [JsonPropertyName("library")]
    public string Library { get; set; } = string.Empty;

    // Python function name to call (e.g., send_command, get_facts)
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    // Module path (e.g., netmiko, napalm, nornir_netmiko)
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    // Subprocess timeout in seconds. Defaults to 300 if <= 0.
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }
}
