using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class DevicePoolResponse
{
    [JsonPropertyName("device_pool_id")]
    public Guid DevicePoolId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("filter_rules")]
    public JsonElement FilterRules { get; set; }

    [JsonPropertyName("static_members")]
    public List<Guid> StaticMembers { get; set; } = new();

    [JsonPropertyName("allow_draft")]
    public bool AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool AllowProduction { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
