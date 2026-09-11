using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Configuration for a transform execution. Wire format of service_config.
// The only supported language today is "jmespath"; handler rejects other values.
// Expression defaults to "*" (passthrough) when empty.
internal class TransformConfig
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "jmespath";

    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;

    // Optional explicit input. If null, handler falls back to req.Input.
    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }
}
