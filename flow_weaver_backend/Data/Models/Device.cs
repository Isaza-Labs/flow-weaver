using System.Text.Json;

namespace flow_weaver_backend.Models;

public class Device : BaseModel
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public string? ExternalId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime LastSyncAt { get; set; }
    public JsonElement Properties { get; set; } = default;
    public Guid CredentialId { get; set; }

    // Which workflow environments may dispatch to this device. A run
    // resolves its targets against the environment of the workflow being
    // run and drops every device that doesn't allow it — see
    // WorkflowExecutor.ResolveTargetsAsync. The three are independent:
    // any combination is valid, including all three (reachable from
    // anywhere) and none (parked — no run can target it, which the
    // executor reports rather than silently skipping).
    //
    // AllowQa is the former IsQaLab flag: before the trio existed, only
    // env=qa filtered, so draft/production reached every device. The
    // migration backfills AllowDraft/AllowProduction to true for that
    // reason — it keeps existing inventories behaving exactly as before.
    public bool AllowDraft { get; set; } = true;
    public bool AllowQa { get; set; }
    public bool AllowProduction { get; set; } = true;

    // Phase 2c: expected SSH host key fingerprint in "SHA256:<base64>"
    // form. When set, SshHandler compares the presented key to this
    // value at connect time and refuses to proceed on mismatch (MITM
    // defense). When null, the handler falls back to a soft-TOFU warning
    // log but still connects — an admin can harden the device row after
    // first successful connect by pasting the fingerprint reported in
    // the step's logs.
    public string? ExpectedSshHostKeyFingerprint { get; set; }
}
