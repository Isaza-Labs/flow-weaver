using System.Text.Json;

namespace flow_weaver_backend.Services.Worker;

/// <summary>
/// Whether a step changed anything, as the handler reports it.
/// </summary>
/// <remarks>
/// Three values, and the third is not "unknown". A run cannot act on "unknown" without picking
/// something, and every reason to pick would be a guess — which is exactly the inference this
/// replaces. <see cref="AuthorDecides"/> says the handler genuinely cannot classify the action
/// because the author supplied it, and names who can: the node's <c>config_overrides.changes</c>
/// where the node carries the action (ssh commands, an mcp tool), or the snippet's own
/// declaration where the snippet carries it (a python script, a playbook).
///
/// A step of such a type with no declaration at either level FAILS, naming what to set. That is
/// louder than the old default, and it is the point: the previous design let nobody answer and
/// answered on their behalf.
/// </remarks>
public enum StepChange
{
    /// <summary>Ran and found nothing to do.</summary>
    Unchanged = 0,

    /// <summary>Ran and mutated something.</summary>
    Changed = 1,

    /// <summary>The handler cannot know; the author declared it, or the step fails.</summary>
    AuthorDecides = 2,
}

// What a handler gives back. The executor maps Success onto the node result and
// stores Output for downstream {{ steps.<node>.output.* }} references.

// What a handler gives back after executing a snippet. The worker writes
// these fields into the step_run row and transitions the step to
// completed / failed accordingly.
public sealed class SnippetResult
{
    public required bool Success { get; init; }

    // JSON payload stored in step_runs.OutputPayload. Handlers should
    // return a well-structured object — the VariableResolver reads it
    // via {{ steps.X.output.field }} to feed downstream steps.
    public JsonElement Output { get; init; } = default;

    // Free-form text appended to step_runs.Logs. Useful for debugging
    // (raw command output, HTTP traces, timing breakdowns).
    public string Logs { get; init; } = string.Empty;

    // Human-readable error string written to step_runs.Error on failure.
    // Empty on success.
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// Whether this step changed anything. REQUIRED — there is no silence.
    /// </summary>
    /// <remarks>
    /// New here, and the same shape and name Nashira uses, because it is contract vocabulary
    /// rather than either product's idea: `workflow-v1/run-outcome` needs a run to be able to
    /// say whether a failure left changes behind, and neither product could answer honestly —
    /// this one had no signal at all, and the other inferred it from the idempotency tier.
    ///
    /// A tier says whether an action COULD be undone. This says whether anything WAS done.
    /// </remarks>
    public required StepChange Change { get; init; }
}
