using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// WorkflowTrigger persistence. Generic CRUD covers the flat list / get / add /
// update / delete; this adds the by-parent-workflow queries (WorkflowId is a
// foreign key, not the PK).
public interface IWorkflowTriggerRepository : IRepository<WorkflowTrigger>
{
    Task<IReadOnlyList<WorkflowTrigger>> ListByWorkflowAsync(
        Guid workflowId, int limit, int offset, CancellationToken ct = default);

    Task<int> CountByWorkflowAsync(Guid workflowId, CancellationToken ct = default);

    // The public webhook ingest runs anonymously and resolves the trigger
    // from the URL id alone, mirroring
    // GitWebhookRepository.FindActiveByIdAsync. AsNoTracking read.
    Task<WorkflowTrigger?> FindActiveByIdAsync(Guid triggerId, CancellationToken ct = default);

    // Tracked variant so the receiver can stamp last-delivery fields.
    Task<WorkflowTrigger?> FindTrackedByIdAsync(Guid triggerId, CancellationToken ct = default);

    // True when any active trigger already uses this route. A bundle import
    // keeps the source route only while it is free here (bundle/SPEC.md §6).
    Task<bool> RouteExistsAsync(string route, CancellationToken ct = default);
}
