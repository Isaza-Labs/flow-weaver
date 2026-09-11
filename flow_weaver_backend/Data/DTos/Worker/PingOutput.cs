using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Structured result of a ping execution. Serialized into the step_run output column.
internal class PingOutput
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("rtt_avg")]
    public string RttAvg { get; set; } = string.Empty;

    [JsonPropertyName("packets_received")]
    public int PacketsReceived { get; set; }

    [JsonPropertyName("raw_output")]
    public string RawOutput { get; set; } = string.Empty;
}
