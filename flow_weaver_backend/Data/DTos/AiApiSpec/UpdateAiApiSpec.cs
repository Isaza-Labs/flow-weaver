using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateAiApiSpec
{
    [JsonPropertyName("api")]
    public string? Api { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; set; }

    // Link to an Integration (null = global). Applied authoritatively on
    // update — the admin edit form always sends the current picker value, so
    // null here unlinks. See AiApiSpecService.UpdateAsync.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }
}
