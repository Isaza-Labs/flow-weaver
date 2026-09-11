using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class UpdateDevice
{
    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

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

    // Which environments may dispatch to this device. Omitted = leave the
    // current value; true / false = explicit set. Any combination is valid,
    // including none — the executor then refuses runs that target it rather
    // than skipping it silently. The DevicePool equivalent lives on
    // UpdateDevicePool.
    [JsonPropertyName("allow_draft")]
    public bool? AllowDraft { get; set; }

    [JsonPropertyName("allow_qa")]
    public bool? AllowQa { get; set; }

    [JsonPropertyName("allow_production")]
    public bool? AllowProduction { get; set; }

    // Expected SSH host key, `SHA256:<base64>`. Omitted = leave as-is;
    // an empty string clears the pin (back to unpinned, which re-arms the
    // auto-pin on the next connect). Re-pinning after a legitimate key
    // rotation is the intended way to clear a mismatch.
    [JsonPropertyName("expected_ssh_host_key_fingerprint")]
    public string? ExpectedSshHostKeyFingerprint { get; set; }
}
