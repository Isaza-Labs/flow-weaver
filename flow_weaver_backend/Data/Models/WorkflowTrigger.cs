using System.Text.Json;

namespace flow_weaver_backend.Models;

public class WorkflowTrigger : BaseModel
{
    // Trigger.Type values the backend acts on. "cron"/"schedule" are fired by
    // SchedulerHostedService; "webhook" is fired by an inbound POST to the
    // public ingest endpoint (WorkflowWebhookReceiver). Free-form for forward-
    // compat, but these three are the ones with behaviour attached.
    public const string TypeWebhook = "webhook";

    public Guid WorkflowTriggerId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Route { get; set; }

    // Webhook auth (type == "webhook"): HMAC-SHA256 secret, encrypted at rest
    // with ICredentialEncryptionService — mirrors GitWebhook.EncryptedSecret.
    // The public ingest verifies X-FlowWeaver-Signature (HMAC over body) or the
    // X-FlowWeaver-Token shared-secret header against it. Never returned by the
    // API (only `has_webhook_secret` and the one-time plaintext on rotate).
    public byte[]? EncryptedSecret { get; set; }

    // Escape hatch mirroring GitWebhook.AllowUnsigned: when no secret is set and
    // this is true, the ingest accepts unauthenticated deliveries (testing).
    // Default false → an unsigned hit against a secret-less webhook is rejected.
    public bool AllowUnsigned { get; set; }
    public string? CronExpression { get; set; }
    public string Timezone { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; } = default;
    public JsonElement InputDefaults { get; set; } = default;
    public bool Enabled { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? NotificationWebhookURL { get; set; }
    public List<string> NotifyOn { get; set; } = new();
    // Devices the scheduled run fires against (GUIDs of Device rows), mirroring
    // RunWorkflowRequest/WorkflowRun. Copied onto each run by the scheduler;
    // empty means no device context (per_device nodes then run once).
    public List<Guid> TargetDevices { get; set; } = new();

    // Webhook only: may the inbound body pick which devices/pools the run
    // fires against (`target_devices` / `target_pools`)?
    //
    // Default false, and that default is the security property. The webhook
    // caller authenticates with the trigger's shared secret, not a user
    // session — the run is enqueued straight through IWorkflowExecutor, so it
    // never passes the environment/resource-scoped RBAC check that a manual
    // run does. Honouring body-supplied targets unconditionally therefore
    // turned a leaked webhook secret into "run this workflow against ANY
    // device in the inventory", production included.
    //
    // Turning this on is a deliberate widening for callers that genuinely need
    // to pick targets per delivery (a CI job remediating the host it just
    // detected). Even then the caller can only NARROW: body targets are
    // intersected with TargetDevices whenever that list is non-empty. See
    // WorkflowWebhookReceiver.ResolveTargets.
    public bool AllowTargetOverride { get; set; }
}
