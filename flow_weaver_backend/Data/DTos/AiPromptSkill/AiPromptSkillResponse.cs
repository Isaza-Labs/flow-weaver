using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Full response for GET /api/admin/prompt-skills/{id}. The list endpoint
// also returns this shape but leaves Content empty — the UI fetches the
// body on demand when the user opens the editor.
public class AiPromptSkillResponse
{
    [JsonPropertyName("ai_prompt_skill_id")]
    public Guid AiPromptSkillId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }

    // Byte length of Content — the UI shows it without needing to count
    // on the client side.
    [JsonPropertyName("size_bytes")]
    public int SizeBytes { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("created_by")]
    public Guid? CreatedBy { get; set; }

    // Null when the skill is a global catalog entry; set when the skill is
    // scoped to an Integration so the agent can pair it with that row's
    // base URL + credentials.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
