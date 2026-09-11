using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateVendorCommand
{
    [JsonPropertyName("device_type")]
    public string DeviceType { get; set; } = string.Empty;

    [JsonPropertyName("vendor_family")]
    public string? VendorFamily { get; set; }

    // "exact" or "pattern". Defaults to "exact" when omitted.
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
