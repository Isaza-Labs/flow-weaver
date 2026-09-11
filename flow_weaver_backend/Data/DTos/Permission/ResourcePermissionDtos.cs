using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Wire shape uses snake_case explicitly because the global JSON naming
// policy is camelCase, and the frontend client sends/expects snake_case
// (see permissions API in client.ts). Without [JsonPropertyName] the
// `subject_id` field on the wire would not bind to `SubjectId` and the
// service would reject the call with "subject_id is required".

public class ResourcePermissionResponse
{
    [JsonPropertyName("resource_permission_id")]
    public Guid ResourcePermissionId { get; set; }

    [JsonPropertyName("resource_type")]
    public string ResourceType { get; set; } = string.Empty;

    [JsonPropertyName("resource_id")]
    public Guid ResourceId { get; set; }

    [JsonPropertyName("subject_type")]
    public string SubjectType { get; set; } = string.Empty;

    [JsonPropertyName("subject_id")]
    public Guid SubjectId { get; set; }

    [JsonPropertyName("subject_username")]
    public string? SubjectUsername { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("granted_by")]
    public Guid? GrantedBy { get; set; }

    [JsonPropertyName("granted_by_username")]
    public string? GrantedByUsername { get; set; }

    [JsonPropertyName("granted_at")]
    public DateTime GrantedAt { get; set; }
}

public class GrantResourcePermissionRequest
{
    // Defaults to "user"; "group" is reserved for the future Groups model.
    [JsonPropertyName("subject_type")]
    public string SubjectType { get; set; } = "user";

    [JsonPropertyName("subject_id")]
    public Guid SubjectId { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}
