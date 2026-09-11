using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Runtime-only credentials passed to service handlers. Contains decrypted secrets.
// Do not persist or return through public endpoints.
public class DeviceCredentials
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("private_key")]
    public string? PrivateKey { get; set; }

    // ssh, snmp_v3, netconf, rest_api
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // port, community, etc.
    [JsonPropertyName("extra")]
    public Dictionary<string, object?>? Extra { get; set; }

    public override string ToString() => $"DeviceCredentials(Type={Type}, Username={Username})";
}
