using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class AiApiSpecResponse
{
    [JsonPropertyName("ai_api_spec_id")]
    public Guid AiApiSpecId { get; set; }

    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("operation_count")]
    public int OperationCount { get; set; }

    [JsonPropertyName("size_bytes")]
    public int SizeBytes { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("created_by")]
    public Guid? CreatedBy { get; set; }

    // Null when the spec is a global catalog entry; set when the spec is
    // scoped to an Integration so the agent can pair its operations with
    // that row's base URL + credentials.
    [JsonPropertyName("integration_id")]
    public Guid? IntegrationId { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
