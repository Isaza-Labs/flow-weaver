using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateSkill
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("triggers")]
    public List<string> Triggers { get; set; } = new();

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("skill_type")]
    public string SkillType { get; set; } = string.Empty;

    [JsonPropertyName("action_config")]
    public JsonElement ActionConfig { get; set; }

    [JsonPropertyName("parameter_mapping")]
    public JsonElement? ParameterMapping { get; set; }

    [JsonPropertyName("examples")]
    public JsonElement? Examples { get; set; }

    [JsonPropertyName("template")]
    public JsonElement? Template { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("confirmation_required")]
    public bool? ConfirmationRequired { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}
