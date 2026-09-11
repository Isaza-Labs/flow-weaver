using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Device context passed to service handlers and embedded in job payloads.
// May contain decrypted credentials — do not log or persist raw.
public class DeviceInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip_address")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("credentials")]
    public DeviceCredentials? Credentials { get; set; }
}
