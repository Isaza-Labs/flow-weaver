using System.Text.Json;

namespace flow_weaver_backend.Models;

// Universal action trail — every business-relevant operation emits one
// row here. Distinct from AuditEvent (which captures entity mutations
// with before/after) and AuthEvent (sign-in specific). Both the Start→
// Complete pattern (with duration_ms) and single-shot Event calls write
// into this table via ITraceLogger.
public class TraceEvent : BaseModel
{
    // UUIDv4 — the table grows fast, but we index on (At DESC)
    // so chronological access doesn't depend on the key being sequential.
    public Guid TraceEventId { get; set; }

    // Null for anonymous / system events (seed, retention sweeper).
    public Guid? UserId { get; set; }

    // Correlation id coming from CorrelationMiddleware — lets us join
    // one trace row back to the Serilog scope that emitted alongside it.
    public string? RequestId { get; set; }

    // Dot-separated, noun-first. Examples:
    //   ai.chat.stream        workflow.run.enqueue      auth.login
    //   tool.call.execute     admin.secret.create       worker.job.claim
    public string Action { get; set; } = string.Empty;

    // High-level bucket for filters and dashboards:
    //   "ai" | "workflow" | "auth" | "admin" | "worker" | "tool" | "system"
    public string Category { get; set; } = string.Empty;

    // "started" | "completed" | "failed" | "timeout"
    // For EventAsync (instantaneous) typically "completed".
    public string Status { get; set; } = string.Empty;

    // Null when Status == "started". Set on Complete/Fail/Timeout.
    public int? DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    // Free-form, indexed by GIN (kept as jsonb via AppDbContext converter).
    public JsonElement Metadata { get; set; } = default;

    public DateTime At { get; set; } = DateTime.UtcNow;
}
