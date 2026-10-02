using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Wire contracts for the granular permission-grant management API.
// Mirrors the Policy DTO shape; `conditions` is the
// same ABAC `when`-style object the resolver matches.
public class CreatePermissionGrant
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("subject_ids")]
    public List<Guid> SubjectIds { get; set; } = new();

    [JsonPropertyName("conditions")]
    public JsonElement Conditions { get; set; }
}

public class UpdatePermissionGrant
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }

    [JsonPropertyName("subject_ids")]
    public List<Guid>? SubjectIds { get; set; }

    [JsonPropertyName("conditions")]
    public JsonElement? Conditions { get; set; }
}

public class PermissionGrantResponse
{
    [JsonPropertyName("permission_grant_id")]
    public Guid PermissionGrantId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    // Built-in bundles are read-only through this API (managed by the seeder /
    // legacy-role dual-write). The UI renders them non-editable.
    [JsonPropertyName("is_builtin")]
    public bool IsBuiltIn { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    [JsonPropertyName("subject_ids")]
    public List<Guid> SubjectIds { get; set; } = new();

    [JsonPropertyName("conditions")]
    public JsonElement Conditions { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

// A catalogue capability as the grant-builder UI consumes it (read-only).
public class CapabilityInfo
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    // Subset of ["environment", "device", "resource"] the capability can be
    // conditioned on — the builder only shows those inputs.
    [JsonPropertyName("conditionable")]
    public List<string> Conditionable { get; set; } = new();
}
