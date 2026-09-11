using flow_weaver_backend.Services.Worker;

namespace flow_weaver_backend.Services.Engine;

/// <summary>What a finished run LEFT BEHIND, as opposed to whether it finished.</summary>
public sealed record RunOutcome(
    string FinalState,                      // completed | rolled_back | failed
    int ChangedCount,
    IReadOnlyList<string> RollbackPlan);    // reversible changed nodes, reverse execution order

/// <summary>One step's contribution to the outcome, in execution order.</summary>
public sealed record StepOutcome(string NodeId, bool Failed, bool Changed, IdempotencyKind Tier);

// The run outcome rule, ported from Nashira's WorkflowExecutor so the two products compute
// the same three answers from the same evidence (`workflow-v1/run-outcome`).
//
// A pure function over the step list, deliberately: it is the part a conformance vector
// pins, and the orchestrator around it — a durable queue, a polling loop, a database — is
// the part that differs between the products and must not leak into the answer.
public static class RunOutcomeCalculator
{
    public static RunOutcome Compute(IReadOnlyList<StepOutcome> ordered)
    {
        var failed = false;
        var changedCount = 0;
        var hasNonReversibleChange = false;

        // Reversible changed nodes, first-seen order, deduplicated.
        //
        // Deduplication is this product's addition and it is not cosmetic: a `per_device`
        // node fans out into one step_run per device, so the same node id arrives several
        // times. A plan naming it once per device would read as several separate reversals
        // of separate things. Nashira has one execution per node and so never meets this.
        var changedReversible = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in ordered)
        {
            if (step.Failed) failed = true;
            if (!step.Changed) continue;

            changedCount++;
            if (Reversible(step.Tier))
            {
                if (seen.Add(step.NodeId)) changedReversible.Add(step.NodeId);
            }
            else
            {
                hasNonReversibleChange = true;
            }
        }

        // The plan exists only for a run that failed — a run that completed has nothing to
        // undo. NOTHING EXECUTES IT in either product: `rolled_back` says every change this
        // run made COULD be undone, not that any of it was.
        var rollbackPlan = failed
            ? changedReversible.AsEnumerable().Reverse().ToList()
            : [];

        var finalState = failed
            ? (hasNonReversibleChange ? "failed" : "rolled_back")
            : "completed";

        return new RunOutcome(finalState, changedCount, rollbackPlan);
    }

    // Reversible = can be automatically rolled back (idempotent replay or compensation).
    // Same predicate as Nashira's `Idempotency.IsReversible`.
    public static bool Reversible(IdempotencyKind kind) => kind != IdempotencyKind.NonReversible;

    public static string ToWire(IdempotencyKind kind) => kind switch
    {
        IdempotencyKind.Idempotent => "idempotent",
        IdempotencyKind.NonReversible => "non_reversible",
        _ => "requires_compensation",
    };
}
