using System.Text.Json;

namespace flow_weaver_backend.Models;

public class WorkflowPlan : BaseModel
{
    public Guid WorkflowPlanId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Intent { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement Steps { get; set; } = default;
    public JsonElement ServicesToCreate { get; set; } = default;
    public JsonElement ServicesToReuse { get; set; } = default;
    public List<Guid> TargetDevices { get; set; } = new();
    public List<Guid> TargetPools { get; set; } = new();
    public JsonElement Risks { get; set; } = default;
    public string Status { get; set; } = PlanStatus.Draft;
    public string? RejectionReason { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public Guid? WorkflowId { get; set; }
    // RESERVED since the initial schema — no code path has ever written it,
    // so it is deliberately NOT in WorkflowPlanResponse (it was a permanent
    // `verification_id: null` in the frontend). Wire a writer before
    // surfacing it again.
    public Guid? VerificationId { get; set; }
}
