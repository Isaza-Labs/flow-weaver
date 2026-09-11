using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class SkillResponse
{
    [JsonPropertyName("skill_id")]
    public Guid SkillId { get; set; }

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
    public JsonElement ParameterMapping { get; set; }

    [JsonPropertyName("examples")]
    public JsonElement Examples { get; set; }

    [JsonPropertyName("template")]
    public JsonElement Template { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("confirmation_required")]
    public bool ConfirmationRequired { get; set; }

    [JsonPropertyName("learned_from")]
    public string LearnedFrom { get; set; } = string.Empty;

    [JsonPropertyName("use_count")]
    public int UseCount { get; set; }

    [JsonPropertyName("success_count")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
