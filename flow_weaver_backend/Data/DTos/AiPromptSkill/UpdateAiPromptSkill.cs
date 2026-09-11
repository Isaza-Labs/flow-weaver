using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// PATCH-style body for /api/admin/prompt-skills/{id}. Every field is
// optional; only supplied fields are written.
public class UpdateAiPromptSkill
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; set; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; set; }

    // Link to an Integration (null = global). Applied authoritatively on
    // update — the admin edit form always sends the current picker value, so
    // null here unlinks. See AiPromptSkillService.UpdateAsync.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }
}
