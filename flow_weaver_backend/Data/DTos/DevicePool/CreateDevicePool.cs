using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateDevicePool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("filter_rules")]
    public JsonElement? FilterRules { get; set; }

    [JsonPropertyName("static_members")]
    public List<Guid>? StaticMembers { get; set; }

    // Environments this pool may be targeted from. Omitted falls back to
    // draft + production, matching an unflagged pool before the trio.
    [JsonPropertyName("allow_draft")]
    public bool? AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool? AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool? AllowProduction { get; set; }
}
