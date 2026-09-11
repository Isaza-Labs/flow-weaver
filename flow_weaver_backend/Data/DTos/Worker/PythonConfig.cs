using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Configuration for a python_snippet execution. Wire format of service_config.
// Handler applies a default timeout of 300s if the parsed value is <= 0.
internal class PythonConfig
{
    // Python source code to execute. Required.
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    // Subprocess timeout in seconds. Defaults to 300 if <= 0.
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 300;
}
