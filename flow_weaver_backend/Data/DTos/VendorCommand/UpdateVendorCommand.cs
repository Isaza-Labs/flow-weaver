using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// All-nullable so callers can PATCH a single field at a time. Service
// layer only writes the fields that arrive non-null.
public class UpdateVendorCommand
{
    [JsonPropertyName("device_type")]
    public string? DeviceType { get; set; }

    [JsonPropertyName("vendor_family")]
    public string? VendorFamily { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
