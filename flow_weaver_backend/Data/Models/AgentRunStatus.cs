namespace flow_weaver_backend.Models;

// String constants matching the DB CHECK constraint on AgentRun.Status.
public static class AgentRunStatus
{
    public const string Running = "running";
    public const string AwaitingInput = "awaiting_input";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string QuotaBlocked = "quota_blocked";
}
