using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// WorkflowAcceptanceTest reads. run_acceptance_tests loads the workflow's
// active tests, grades each against a candidate run, then mutates LastStatus /
// LastRunId / LastFailures in place and saves — so the rows must be TRACKED.
public interface IWorkflowAcceptanceTestRepository : IRepository<WorkflowAcceptanceTest>
{
    // Active acceptance tests for one workflow, TRACKED so the
    // grader can update each row's denormalized last-run fields and persist.
    Task<IReadOnlyList<WorkflowAcceptanceTest>> ListTrackedByWorkflowAsync(
        Guid workflowId, CancellationToken ct = default);
}
