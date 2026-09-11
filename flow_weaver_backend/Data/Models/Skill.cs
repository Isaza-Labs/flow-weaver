using System.Text.Json;

namespace flow_weaver_backend.Models;

public class Skill : BaseModel
{
    public Guid SkillId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Triggers { get; set; } = new();
    public string? Description { get; set; }
    public string SkillType { get; set; } = string.Empty;
    public JsonElement ActionConfig { get; set; } = default;
    public JsonElement ParameterMapping { get; set; } = default;
    public JsonElement Examples { get; set; } = default;
    public JsonElement Template { get; set; } = default;
    public JsonElement Parameters { get; set; } = default;
    public bool Enabled { get; set; }
    public bool ConfirmationRequired { get; set; }
    public string LearnedFrom { get; set; } = string.Empty;
    public int UseCount { get; set; }
    public int SuccessCount { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? CreatedBy { get; set; }
}
