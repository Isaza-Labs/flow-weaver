namespace flow_weaver_backend.Models;

// One generated report file, persisted for audit. Originates from one of
// three sources:
//   - "agent"    : the LLM agent called the generate_report tool; we
//                  record which conversation and the user message that
//                  triggered it (encrypted at rest + secret-redacted).
//   - "api"      : a direct POST /api/reports call by an authenticated
//                  user. No agent context; just the requesting user.
//   - "workflow" : a snippet inside a workflow run produced the report.
//                  AgentPrompt* fields stay null; we record WorkflowRunId
//                  instead so /admin/reports can cross-link to the run.
//
// Storage layout:
//   - ContentBytes is gzipped for compressible formats (html/csv/pdf) and
//     raw for xlsx (already ZIP-packed). IsCompressed flags which.
//   - SizeBytes is the ORIGINAL size; StoredBytes is what actually lives
//     in postgres. The list view shows SizeBytes because that's what the
//     user downloads.
//   - AgentPromptEncrypted wraps the redacted user message with the same
//     DataProtection provider that protects credentials/secrets.
//
// Lifecycle:
//   - ExpiresAt defaults to CreatedAt + 90 days (configurable in
//     appsettings:Reports.RetentionDays).
//   - ReportRetentionHostedService soft-deletes expired rows, then
//     hard-deletes them 7 days later.
public class ReportArtifact : BaseModel
{
    public Guid ReportArtifactId { get; set; }

    public Guid? UserId { get; set; }

    // ─── file metadata ──────────────────────────────────────────────────

    public string Title { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;    // html|csv|xlsx|pdf

    /// <summary>Size of the original file before compression.</summary>
    public int SizeBytes { get; set; }

    /// <summary>Actual byte count written to postgres (gzipped for html/csv/pdf).</summary>
    public int StoredBytes { get; set; }

    public bool IsCompressed { get; set; }

    public byte[] ContentBytes { get; set; } = Array.Empty<byte>();

    public string Sha256 { get; set; } = string.Empty;

    // ─── retention ──────────────────────────────────────────────────────

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(90);

    // ─── origin ─────────────────────────────────────────────────────────

    /// <summary>agent | api | workflow</summary>
    public string Source { get; set; } = "api";

    public Guid? AgentConversationId { get; set; }
    public Guid? AgentRunId { get; set; }
    public string? AgentName { get; set; }

    /// <summary>Redacted user message, encrypted at rest. Decrypted only
    /// on the detail endpoint (and every decrypt leaves an audit row).</summary>
    public byte[]? AgentPromptEncrypted { get; set; }

    /// <summary>True when SecretRedactor removed at least one token from the prompt.</summary>
    public bool AgentPromptRedacted { get; set; }

    public Guid? WorkflowRunId { get; set; }
    public Guid? WorkflowId { get; set; }

    public string? RequestId { get; set; }
}
