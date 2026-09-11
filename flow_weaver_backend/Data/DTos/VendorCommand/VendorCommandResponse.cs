using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class VendorCommandResponse
{
    [JsonPropertyName("vendor_command_id")]
    public Guid VendorCommandId { get; set; }

    [JsonPropertyName("device_type")]
    public string DeviceType { get; set; } = string.Empty;

    [JsonPropertyName("vendor_family")]
    public string VendorFamily { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    // "seed" rows are part of the baseline catalog; UI shows them
    // read-only-ish (admin can still edit/delete, but a re-seed will
    // skip them so deleted seed rows stay deleted). "user" rows survive
    // re-seed regardless.
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
