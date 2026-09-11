using System.Text.Json;

namespace flow_weaver_backend.Services.Policy;

// The context a policy needs to decide whether to block an operation.
// Populated by the caller (WorkflowService.Update, WorkflowExecutor.Run,
// PromotionService.Promote, etc.) from whatever signals it has on hand;
// unknown fields stay null and the matcher treats "null" as "any" so
// rules don't accidentally match more than intended.
//
// Phase 3 (gates): for `action="promote"` the caller now also supplies
// `WorkflowId` (the workflow being promoted — needed by the
// successful_runs / last_successful_run_within evaluators) and
// `TargetEnvironment` (where it's heading — gates can fire only on
// specific transitions, e.g. qa→production).
public sealed record PolicyEvaluationContext(
    string Action,                  // "create" | "update" | "run" | "promote" | "ssh_exec"
    string Environment,             // current env: draft | qa | production
    string? WorkflowName,
    string? WorkflowDescription,
    JsonElement Nodes,              // workflow.Nodes — evaluator reads snippet_type
    IReadOnlyList<string> DeviceRoles,
    IReadOnlyList<string> DevicePoolNames,
    // Phase 2d: per-command SSH gate. Populated by SshHandler right
    // before each RunCommand so a `when.ssh_command_regex` rule can
    // block destructive shell verbs ("reload", "write erase") without
    // needing to inspect the workflow itself. Null for every other
    // action so existing rules keep their "any" semantics.
    string? SshCommand = null,
    // Phase 3: gate context. WorkflowId lets aggregating gates query
    // historical runs/steps for the same workflow; TargetEnvironment
    // is the destination of a promotion (current Environment is the
    // source). Null for non-promote actions.
    Guid? WorkflowId = null,
    string? TargetEnvironment = null);

public sealed record PolicyDecision(
    bool Allowed,
    string? PolicyName,
    string? Reason);

public interface IPolicyEvaluator
{
    // Returns the FIRST deny that matches, or Allowed=true when no
    // enabled policy blocks the operation. Deterministic ordering by
    // CreatedAt keeps the "which rule tripped" explanation stable as
    // policies get added.
    Task<PolicyDecision> EvaluateAsync(
        PolicyEvaluationContext context, CancellationToken ct);
}
