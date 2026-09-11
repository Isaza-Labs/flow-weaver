using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// One operation parsed from a YAML spec in /Specs/. The filename (minus .yaml)
// becomes the Api prefix; every path+method pair becomes an ApiOperation.
// In-memory shape used by the agent's discover/detail/execute tools — the
// DB-backed Integration + IntegrationAction entities are the runtime
// equivalent once a spec is explicitly imported into the database.
public class ApiOperation
{
    [JsonPropertyName("operation_id")]
    public string OperationId { get; set; } = string.Empty;

    // Filename stem, e.g. "netbox" for netbox.yaml. Used as the tool prefix.
    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    // Uppercase HTTP verb: GET / POST / PATCH / DELETE.
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}
