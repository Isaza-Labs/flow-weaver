using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Response of POST /integration/{id}/discover. Covers both paths:
//   - Built-in template import    : { imported, message }
//   - OpenAPI / ServiceNow import : { imported, total }
// Uses nullable fields so the contract stays stable regardless of which
// path ran.
public class IntegrationDiscoveryResult
{
    [JsonPropertyName("imported")]
    public int Imported { get; set; }

    // Total actions parsed from the spec (set only on schema-based discovery).
    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; set; }

    // Human-readable summary (set on template-based discovery).
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
