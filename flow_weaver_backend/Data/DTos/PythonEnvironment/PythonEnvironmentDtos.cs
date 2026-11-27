using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos.PythonEnvironments;

// Admin request to define a uv-built Python environment. The builder on the
// worker creates it asynchronously; the row is `pending` until then.
public class CreatePythonEnvironment
{
    // Slug, also the directory name: ^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$.
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // Passed to `uv venv --python`. Null/blank = the image's python3.
    [JsonPropertyName("python_version")]
    public string? PythonVersion { get; set; }

    // requirements.txt content.
    [JsonPropertyName("requirements")]
    public string Requirements { get; set; } = string.Empty;

    // The environment the SSH runner retries with when the image's paramiko
    // cannot negotiate a host key. At most one environment may set it.
    [JsonPropertyName("is_ssh_fallback")]
    public bool? IsSshFallback { get; set; }
}

// PATCH-style: every field is optional; omitted keeps the current value.
// Changing requirements or python_version puts the environment back to
// `pending` so the builder rebuilds it.
public class UpdatePythonEnvironment
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // "" clears it back to the image's python3.
    [JsonPropertyName("python_version")]
    public string? PythonVersion { get; set; }

    // Credentials in index URLs come back masked (`https://***@host`). A line
    // sent back still masked is restored from the stored requirements, so an
    // edit form can round-trip the text it was given.
    [JsonPropertyName("requirements")]
    public string? Requirements { get; set; }

    [JsonPropertyName("is_ssh_fallback")]
    public bool? IsSshFallback { get; set; }
}

public class PythonEnvironmentResponse
{
    [JsonPropertyName("python_environment_id")]
    public Guid PythonEnvironmentId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("python_version")]
    public string? PythonVersion { get; set; }

    // Masked: URL credentials replaced by `***`.
    [JsonPropertyName("requirements")]
    public string Requirements { get; set; } = string.Empty;

    [JsonPropertyName("is_ssh_fallback")]
    public bool IsSshFallback { get; set; }

    // pending | building | ready | failed.
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    // `uv pip freeze` of the last successful build (masked).
    [JsonPropertyName("resolved_packages")]
    public string? ResolvedPackages { get; set; }

    [JsonPropertyName("requirements_hash")]
    public string? RequirementsHash { get; set; }

    [JsonPropertyName("built_at")]
    public DateTime? BuiltAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
