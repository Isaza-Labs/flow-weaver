using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Rejection carries a reason for the audit trail.
public class RejectPlan
{
    [JsonPropertyName("approved_by")]
    public string ApprovedBy { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
