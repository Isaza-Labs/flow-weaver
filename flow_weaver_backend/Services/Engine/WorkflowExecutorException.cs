namespace flow_weaver_backend.Services.Engine;

// Thrown by the executor for domain-level errors (workflow not found,
// service definition missing, target resolution failure). NOT for
// infrastructure errors (DB connectivity, serialization) — those
// propagate unwrapped and are caught by the top-level try/catch in
// RunOrchestrationAsync.
public sealed class WorkflowExecutorException : Exception
{
    public WorkflowExecutorException(string message) : base(message) { }
}
