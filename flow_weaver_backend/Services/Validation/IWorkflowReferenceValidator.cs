using System.Text.Json;

namespace flow_weaver_backend.Services.Validation;

// Second-pass validation that runs after the JSON-schema check.
//
// The schema only enforces DAG shape and declares `config_overrides` as
// an open object — so it can't tell a real integration_id / action_id
// GUID apart from a placeholder like "netbox-list-active-devices".
// That gap let the agent persist workflows that parse-validated fine
// but could never execute, producing an opaque runtime error and
// pushing the agent into a fix/re-propose loop.
//
// This validator closes the loop by confirming, for every node whose
// snippet is an `integration_action`, that:
//   - config_overrides.integration_id is a GUID for an active Integration,
//     and
//   - config_overrides.action_id is a GUID for an active IntegrationAction
//     that belongs to that Integration.
//
// Anything else is rejected up-front with an actionable message — the
// user/agent gets told exactly which node and which field is wrong.
public interface IWorkflowReferenceValidator
{
    // `previousNodes` is the graph as currently stored, when there is one. The
    // permission gates (a git write, a secret reference) only apply to what this
    // write ADDS: a node that already had the same write operation, or already
    // referenced the same secret, is left alone — otherwise editing any other
    // node of an admin-authored workflow would need the admin's permissions.
    Task<WorkflowValidationResult> ValidateAsync(
        JsonElement nodes, CancellationToken ct, JsonElement? previousNodes = null);

    // Same DAG-reference checks as ValidateAsync, plus heuristic
    // warnings that compare the workflow's stated purpose (name +
    // description) against the snippet types it actually uses. Catches
    // the "workflow says 'send email' but has no integration_action"
    // anti-pattern. Warnings never block the write; errors do.
    Task<WorkflowValidationResult> ValidateWithContextAsync(
        JsonElement nodes,
        string? workflowName,
        string? workflowDescription,
        CancellationToken ct,
        JsonElement? previousNodes = null);
}
