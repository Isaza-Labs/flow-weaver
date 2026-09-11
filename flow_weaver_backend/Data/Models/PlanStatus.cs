namespace flow_weaver_backend.Models;

// String constants matching the DB CHECK constraint on WorkflowPlan.Status.
public static class PlanStatus
{
    public const string Draft = "draft";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Building = "building";
    public const string Built = "built";
    public const string Failed = "failed";
}
