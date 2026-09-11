using System.Text.Json;

namespace flow_weaver_backend.Models;

// Append-only audit log for authentication events. Written by IAuthService
// on every login / refresh / logout / lockout / password change. Queryable
// via GET /api/auth/events by admins.
//
// UserId is nullable because failed logins with an unknown username can't be
// attributed (but still need to be logged for anomaly detection).
public class AuthEvent
{
    public Guid AuthEventId { get; set; }
    public Guid? UserId { get; set; }

    // One of: login_success, login_failure, logout, password_change,
    // lockout, refresh, token_revoked. Strings so we can add new events
    // without a DB migration.
    public string Event { get; set; } = string.Empty;

    public string Ip { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;

    // Free-form structured context: attempted_username, reason, chain_id, etc.
    public JsonElement Metadata { get; set; }
}
