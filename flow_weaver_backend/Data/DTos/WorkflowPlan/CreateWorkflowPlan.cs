using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// POST /api/v1/ai/plans body. Status is always 'draft' on creation —
// submit_for_approval is a separate transition.
public class CreateWorkflowPlan
{
    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("steps")]
    public JsonElement? Steps { get; set; }

    [JsonPropertyName("services_to_create")]
    public JsonElement? ServicesToCreate { get; set; }

    [JsonPropertyName("services_to_reuse")]
    public JsonElement? ServicesToReuse { get; set; }

    [JsonPropertyName("target_devices")]
    public List<Guid>? TargetDevices { get; set; }

    [JsonPropertyName("target_pools")]
    public List<Guid>? TargetPools { get; set; }

    [JsonPropertyName("risks")]
    public JsonElement? Risks { get; set; }
}
