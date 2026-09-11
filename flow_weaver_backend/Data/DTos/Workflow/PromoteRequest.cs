using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class PromoteRequest
{
    [JsonPropertyName("target_environment")]
    public string TargetEnvironment { get; set; } = string.Empty;

    [JsonPropertyName("change_summary")]
    public string ChangeSummary { get; set; } = string.Empty;

    [JsonPropertyName("promoted_by")]
    public string? PromotedBy { get; set; }

    [JsonPropertyName("approved_by")]
    public string? ApprovedBy { get; set; }
}
