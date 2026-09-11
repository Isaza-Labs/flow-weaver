using System.Text.Json;

namespace flow_weaver_backend.Models;

// IsActive (from BaseModel) = soft-delete; Enabled = toggleable by the user
// without deleting the action definition. Both coexist intentionally.
public class IntegrationAction : BaseModel
{
    public Guid IntegrationActionId { get; set; }
    public Guid IntegrationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public JsonElement PathParams { get; set; } = default;
    public JsonElement QueryParams { get; set; } = default;
    public JsonElement RequestBody { get; set; } = default;
    public JsonElement ResponseSchema { get; set; } = default;
    public string Category { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
