using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Engine;

public interface IWorkflowExecutor
{
    // Creates a WorkflowRun row (status=pending) and enqueues a
    // `type=workflow_run` job so a worker eventually picks it up and
    // calls ExecuteRunAsync. Returns the new run id.
    //
    // Validates the DAG eagerly so the caller gets a fast 400 on
    // structural errors (cycles, invalid types) instead of discovering
    // the problem asynchronously in the worker log.
    Task<Guid> EnqueueRunAsync(
        Guid userId,
        Guid workflowId,
        RunWorkflowRequest request,
        CancellationToken ct,
        string trigger = "manual");

    // Long-running orchestration body. Called by the worker when it
    // claims a `type=workflow_run` job. Walks the DAG topologically:
    // enqueues start steps, polls step_runs for completion, enqueues
    // successors via Dag.NextNodes, marks the run terminal when all
    // steps are done.
    //
    // Bounded by SemaphoreSlim (MaxConcurrentWorkflows) and a hard
    // wall-clock timeout (OrchestrationTimeoutSeconds).
    //
    // jobId/workerId are passed through so the polling loop can heartbeat
    // its own claim — orchestration of large fan-outs routinely outlives
    // the 5-minute default lease, and without renewal JobReclaim would
    // resurrect the run under a second worker, which in turn would
    // duplicate every step_run created so far.
    Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null);
}
