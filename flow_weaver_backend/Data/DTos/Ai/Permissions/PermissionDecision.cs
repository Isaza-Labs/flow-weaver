using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Output of the permission classifier for a single tool call.
// Reason is human-readable and surfaced in the chat UI when Action is Ask or Deny.
public class PermissionDecision
{
    [JsonPropertyName("action")]
    public PermissionAction Action { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
