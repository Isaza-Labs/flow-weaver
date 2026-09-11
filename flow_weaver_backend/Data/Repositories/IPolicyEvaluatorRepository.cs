using System.Text.Json;

namespace flow_weaver_backend.Data.Repositories;

// One enabled policy's identity + rule JSON, as the evaluator consumes it.
public sealed record PolicyRuleRow(Guid PolicyId, string Name, JsonElement Rule);

// Read-only data access for PolicyEvaluator. Spans Policies + WorkflowRuns +
// StepRuns (the gate requirements query historical run state), so it is a
// dedicated query repository rather than an IRepository<T>. The evaluator keeps
// all rule-shape/business logic; this only runs the SQL.
public interface IPolicyEvaluatorRepository
{
    // Enabled, active policies, oldest first (deterministic evaluation
    // order).
    Task<IReadOnlyList<PolicyRuleRow>> GetEnabledPoliciesAsync(CancellationToken ct = default);

    // Count of completed workflow runs. workflowId null = any workflow;
    // completedSince null = no time window.
    Task<int> CountCompletedRunsAsync(
        Guid? workflowId, DateTime? completedSince, CancellationToken ct = default);

    // Most recent completed-run timestamp, or null. workflowId null = any workflow.
    Task<DateTime?> GetLastCompletedRunAsync(
        Guid? workflowId, CancellationToken ct = default);

    // Count of completed step runs. snippetIds empty = any snippet;
    // completedSince null = no time window.
    Task<int> CountCompletedStepRunsAsync(
        IReadOnlyList<Guid> snippetIds, DateTime? completedSince, CancellationToken ct = default);
}
