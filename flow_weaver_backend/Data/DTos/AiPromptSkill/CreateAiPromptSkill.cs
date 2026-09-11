using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Upsert payload for /api/admin/prompt-skills. The frontend reads the
// uploaded .md file, passes its raw content as a string here. If `name`
// already exists the service updates in place; otherwise it inserts.
public class CreateAiPromptSkill
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    // Defaults to 100 on create so base.md (0) always leads. Pass 0
    // explicitly if you are uploading a replacement base file.
    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; set; }

    // Optional link to an Integration. Null = global catalog entry; set to
    // scope this skill to one integration (its content is paired with that
    // integration's base URL + credentials). The admin UI's picker sends it.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }
}
