using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// PUT /api/v1/ai/plans/{id} body. Only valid while the plan is in
// 'draft' status — enforce in the store/service guard logic.
public class UpdateWorkflowPlan
{
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

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
