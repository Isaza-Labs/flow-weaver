using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Approval is a separate API call so the state transition is explicit (gate 1 — plan).
public class ApprovePlan
{
    [JsonPropertyName("approved_by")]
    public string ApprovedBy { get; set; } = string.Empty;
}
