using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class CreateDevice
{
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
    public JsonElement? Properties { get; set; }

    [JsonPropertyName("credential_id")]
    public Guid? CredentialId { get; set; }

    // Which environments may dispatch to this device. Omitted falls back to
    // the same shape an unflagged device had before the trio existed:
    // reachable from draft and production, not from qa.
    [JsonPropertyName("allow_draft")]
    public bool? AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool? AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool? AllowProduction { get; set; }

    // Expected SSH host key, `SHA256:<base64>` (what `ssh-keygen -lf` prints,
    // and what an ssh step reports as `host_key_fingerprint`). When set, the
    // runner refuses to connect if the presented key differs — the MITM
    // defense for device credentials. Omitted leaves it unpinned; the first
    // successful connect then pins it automatically unless
    // Ssh:AutoPinHostKeyOnFirstUse is turned off.
    [JsonPropertyName("expected_ssh_host_key_fingerprint")]
    public string? ExpectedSshHostKeyFingerprint { get; set; }
}
