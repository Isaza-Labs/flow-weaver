namespace flow_weaver_backend.Models;

/// <summary>
/// Append-only audit log for domain mutations (create, update, delete on any
/// persisted entity). Differs from <see cref="AuthEvent"/> which tracks
/// authentication-specific events (login, logout, lockout, etc.).
/// </summary>
public class AuditEvent
{
    public Guid AuditEventId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>
    /// Human-readable actor label. For a signed-in request this is the
    /// username; for automation it is the synthetic identity the flow bound
    /// on its scope — <c>git-webhook</c>, <c>workflow-webhook</c>,
    /// <c>workflow-runner</c>, <c>messaging-send</c>.
    ///
    /// Needed because <see cref="UserId"/> is null for every automation path
    /// (nobody is signed in), which used to collapse "a scheduled run pushed
    /// to Git" and "an inbound webhook rewrote a workflow" into the same
    /// indistinguishable "user: null" row.
    /// </summary>
    public string? Actor { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public System.Text.Json.JsonElement BeforeJson { get; set; } = default;
    public System.Text.Json.JsonElement AfterJson { get; set; } = default;
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime At { get; set; }

    /// <summary>
    /// Correlation id shared with <see cref="TraceEvent.RequestId"/> and the
    /// <c>request_id</c> property on every Serilog line of the same request.
    /// </summary>
    public string? RequestId { get; set; }
}
