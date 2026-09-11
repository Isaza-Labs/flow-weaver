using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class WorkflowPlanResponse
{
    [JsonPropertyName("workflow_plan_id")]
    public Guid WorkflowPlanId { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("steps")]
    public JsonElement Steps { get; set; }

    [JsonPropertyName("services_to_create")]
    public JsonElement ServicesToCreate { get; set; }

    [JsonPropertyName("services_to_reuse")]
    public JsonElement ServicesToReuse { get; set; }

    [JsonPropertyName("target_devices")]
    public List<Guid> TargetDevices { get; set; } = new();

    [JsonPropertyName("target_pools")]
    public List<Guid> TargetPools { get; set; } = new();

    [JsonPropertyName("risks")]
    public JsonElement Risks { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("rejection_reason")]
    public string? RejectionReason { get; set; }

    [JsonPropertyName("approved_by")]
    public string? ApprovedBy { get; set; }

    [JsonPropertyName("approved_at")]
    public DateTime? ApprovedAt { get; set; }

    [JsonPropertyName("executed_at")]
    public DateTime? ExecutedAt { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    // `verification_id` is deliberately absent: WorkflowPlan.VerificationId
    // has never been written by any code path (reserved column) — surfacing
    // it was a permanent null in the frontend.

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
