using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateDevicePool
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("filter_rules")]
    public JsonElement? FilterRules { get; set; }

    [JsonPropertyName("static_members")]
    public List<Guid>? StaticMembers { get; set; }

    // Environments this pool may be targeted from. Applied on top of the
    // per-device flags: a run must be allowed by the pool AND by the member.
    [JsonPropertyName("allow_draft")]
    public bool? AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool? AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool? AllowProduction { get; set; }
}
