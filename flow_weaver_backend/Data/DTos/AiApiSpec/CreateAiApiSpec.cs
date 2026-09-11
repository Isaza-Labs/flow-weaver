using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Upsert payload for /api/admin/api-specs. `api` is the filename stem
// without `.yaml` (e.g. "netbox"); it doubles as the agent-facing
// identifier passed to discover(api="..."). `content` is raw YAML.
public class CreateAiApiSpec
{
    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    // Optional link to an Integration. Null = global catalog entry; set to
    // scope this spec to one integration so the agent knows which base URL +
    // credentials to use for its operations. The admin UI's picker sends it.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }
}
