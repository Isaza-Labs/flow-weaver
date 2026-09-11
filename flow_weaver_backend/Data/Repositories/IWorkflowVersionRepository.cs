using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// WorkflowVersion persistence. Adds the by-parent-workflow queries the
// generic repository can't express (WorkflowId is a foreign key, not the PK,
// and versions are ordered by Version rather than CreatedAt).
public interface IWorkflowVersionRepository : IRepository<WorkflowVersion>
{
    // Versions of one workflow, newest version first. The caller verifies the
    // parent workflow exists first (IRepository<Workflow>.ExistsAsync).
    Task<IReadOnlyList<WorkflowVersion>> ListByWorkflowAsync(
        Guid workflowId, int limit, int offset, CancellationToken ct = default);

    Task<int> CountByWorkflowAsync(Guid workflowId, CancellationToken ct = default);

    // A specific version of a workflow (the rollback target snapshot), or null.
    Task<WorkflowVersion?> GetByWorkflowAndVersionAsync(
        Guid workflowId, int version, CancellationToken ct = default);
}
