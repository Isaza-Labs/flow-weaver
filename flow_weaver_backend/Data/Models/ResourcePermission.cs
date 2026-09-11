namespace flow_weaver_backend.Models;

// Grants a specific user (or, in a future iteration, group) one of the
// resource-scoped roles on a single Workflow or Integration. Multiple
// rows can apply to the same subject + resource combination — the
// effective role is the highest one (owner > editor > runner > viewer).
//
// The global RBAC tier (admin / operator / viewer) keeps acting as a
// floor: an admin always passes a permission check regardless of
// per-resource grants. The per-resource model only RAISES privileges
// for non-admin subjects.
public class ResourcePermission : BaseModel
{
    public Guid ResourcePermissionId { get; set; }

    // What the row protects.
    //
    //   "workflow"     → ResourceId is a WorkflowId
    //   "integration"  → ResourceId is an IntegrationId
    //
    // Open enum so we can extend (e.g. "device_pool") without a schema
    // migration; service layer rejects unknown values.
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }

    // For now subjects are users only. Adding "group" later means a new
    // Groups table and a join — the column is here so the API contract
    // doesn't change later.
    public string SubjectType { get; set; } = "user";
    public Guid SubjectId { get; set; }

    // owner   → full control, including grant/revoke and delete
    // editor  → mutate the resource (edit nodes, change config)
    // runner  → execute the resource (run workflows, test integrations)
    // viewer  → read-only
    public string Role { get; set; } = string.Empty;

    // Audit who did the grant. We keep this denormalised because the
    // actor may later be deleted; we still want forensic trace of who
    // assigned access.
    public Guid? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; }
}
