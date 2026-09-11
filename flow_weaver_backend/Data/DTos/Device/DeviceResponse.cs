using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class DeviceResponse
{
    [JsonPropertyName("device_id")]
    public Guid DeviceId { get; set; }

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("os_version")]
    public string? OsVersion { get; set; }

    [JsonPropertyName("site")]
    public string? Site { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("source_id")]
    public Guid? SourceId { get; set; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("last_sync_at")]
    public DateTime? LastSyncAt { get; set; }

    [JsonPropertyName("properties")]
    public JsonElement Properties { get; set; }

    [JsonPropertyName("credential_id")]
    public Guid? CredentialId { get; set; }

    [JsonPropertyName("allow_draft")]
    public bool AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool AllowProduction { get; set; }

    // Null when the device is unpinned (any host key is accepted until the
    // first connect pins one). Not a secret — a host key fingerprint is
    // public by construction — so it is returned in full.
    [JsonPropertyName("expected_ssh_host_key_fingerprint")]
    public string? ExpectedSshHostKeyFingerprint { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
