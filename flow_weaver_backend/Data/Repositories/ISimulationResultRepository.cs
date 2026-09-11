using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// SimulationResult persistence. simulate_workflow_run stages a new row via
// Add/Save; mark_workflow_ready re-reads the workflow's last simulation by id
// to verify the gate. The by-id read keeps the active filter the original
// handler applied.
public interface ISimulationResultRepository : IRepository<SimulationResult>
{
    // Active simulation row by id (read-only), or null.
    Task<SimulationResult?> FindActiveByIdAsync(
        Guid simulationResultId, CancellationToken ct = default);
}
