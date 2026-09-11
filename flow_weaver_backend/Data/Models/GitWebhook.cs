using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

// Inbound webhook tied to a registered Git repository. The remote
// (GitHub / GitLab / a custom emitter) POSTs to a public URL whose
// last segment is the WebhookId. Signature verification uses Secret
// (encrypted at rest) plus a Provider-specific HMAC scheme.
//
// On a verified delivery whose ref matches OnPushBranches (empty list
// = match every branch), the receiver:
//   • optionally pulls the repo into FlowWeaver (AutoPull),
//   • optionally enqueues a run of OnPushWorkflowId (when set).
public class GitWebhook : BaseModel
{
    public const string ProviderGithub = "github";
    public const string ProviderGitlab = "gitlab";
    public const string ProviderGeneric = "generic";

    public Guid GitWebhookId { get; set; }
    public Guid GitRepositoryId { get; set; }
    public string Name { get; set; } = string.Empty;

    // github | gitlab | generic. Determines which header carries the
    // signature and how it's verified.
    public string Provider { get; set; } = ProviderGithub;

    // HMAC secret. GitHub: shared secret used for HMAC-SHA256 of body.
    // GitLab: token compared via constant-time string equality. Generic:
    // shared secret used for HMAC-SHA256 of body, header X-FlowWeaver-Signature.
    [JsonIgnore]
    public byte[]? EncryptedSecret { get; set; }

    public Guid? OnPushWorkflowId { get; set; }

    // Branch filter — empty list matches every push. Branch names are
    // matched literally against the short ref name (e.g. "main"). Wildcards
    // are NOT supported in v1 — keeps signature/parsing simple.
    public List<string> OnPushBranches { get; set; } = new();

    public bool AutoPull { get; set; } = true;
    public bool Enabled { get; set; } = true;

    // When EncryptedSecret is null/empty, the receiver normally rejects
    // the delivery — accepting unauthenticated POSTs would let anyone
    // who knows the public URL trigger workflow runs. Set this flag on
    // a per-webhook basis to opt out of that gate (testing scenarios,
    // air-gapped emitters that can't sign). Defaults to FALSE so the
    // safe path is the default.
    public bool AllowUnsigned { get; set; } = false;

    public DateTime? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }
}

// Audit row for every webhook hit (verified or rejected). Capped via
// retention sweeper — keeping forever would balloon the table on a busy
// repo. Body and headers are deliberately NOT persisted (PII risk +
// size); we keep just the metadata that explains what happened.
public class GitWebhookDelivery : BaseModel
{
    public Guid GitWebhookDeliveryId { get; set; }
    public Guid GitWebhookId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    // received | verified | rejected | dispatched | failed
    public string Status { get; set; } = string.Empty;

    // push | ping | other — best-effort parse from the request.
    public string? Event { get; set; }
    public string? Branch { get; set; }
    public string? CommitSha { get; set; }
    public Guid? WorkflowRunId { get; set; }
    public string? Error { get; set; }
}
