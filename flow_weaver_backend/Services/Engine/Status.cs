namespace flow_weaver_backend.Services.Engine;

// Canonical lifecycle status strings for the three runtime entities. Using
// static const instead of an enum because the DB stores plain text, the
// JSON responses carry plain text, and the queue repository SQL compares
// plain text — an enum would add conversion noise at every boundary for
// no runtime benefit.

public static class RunStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class StepStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

public static class JobStatus
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

// S15.1 — Integration health/availability status. Surfaced in the UI and
// consulted by IntegrationActionHandler / WorkflowExecutor before any
// outbound HTTP. `needs_config` means an admin or the import wizard
// created the row but credentials are still missing; runs that touch
// such integrations fail at validate-time with a clear error rather
// than at dispatch with a 401 from the upstream system.
public static class IntegrationStatus
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    // Reachable, but the configured credentials were never validated by any
    // probed endpoint (they only answer anonymously) — an invalid token
    // would go unnoticed. See IntegrationHealthChecker's auth verification.
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string NeedsConfig = "needs_config";
}
