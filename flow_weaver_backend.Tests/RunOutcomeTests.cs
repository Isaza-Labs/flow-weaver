using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Worker;
using Xunit;

namespace flow_weaver_backend.Tests;

// The run outcome model (`workflow-v1/run-outcome`), in two halves:
//
//   1. StepChangeResolution — who gets to say whether a step changed anything.
//   2. RunOutcomeCalculator — what the run reports once every step has said.
//
// Both follow the shared contract, so the tests follow its reasoning too. What they
// are guarding is one specific regression: the old model had NO change signal here at all,
// so anything that computed a "final state" would have been computing it from whether steps
// succeeded. These tests exist to keep the answer measured.
public class RunOutcomeTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    // ── who declares ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StepChange.Changed, true)]
    [InlineData(StepChange.Unchanged, false)]
    public void A_handler_that_answers_is_believed(StepChange reported, bool expected)
    {
        // Nothing overrides a measurement. An author who declares `changes: false` on a step
        // whose handler watched it write does not get to say so.
        Assert.Equal(expected, StepChangeResolution.Resolve(
            reported, snippetDeclares: !expected, stepInput: Input("""{"changes": false}""")));
    }

    [Fact]
    public void Where_the_handler_cannot_know_the_node_declares()
    {
        Assert.True(StepChangeResolution.Resolve(
            StepChange.AuthorDecides, snippetDeclares: null, stepInput: Input("""{"changes": true}""")));
    }

    [Fact]
    public void The_node_wins_over_the_snippet_being_more_specific()
    {
        // The same ssh snippet runs `show version` on one node and `configure terminal` on
        // another, so the node is where the action actually lives.
        Assert.False(StepChangeResolution.Resolve(
            StepChange.AuthorDecides, snippetDeclares: true, stepInput: Input("""{"changes": false}""")));
    }

    [Fact]
    public void The_snippet_declares_for_the_types_whose_code_it_carries()
    {
        // A python script or a playbook lives on the snippet row, so its author declares there.
        Assert.True(StepChangeResolution.Resolve(
            StepChange.AuthorDecides, snippetDeclares: true, stepInput: Input("{}")));
    }

    [Fact]
    public void Nobody_declaring_is_a_defect_not_a_default()
    {
        // The whole point. Null means the step fails and names what to set — louder than a
        // guess, and true. This product had no guess to fall back on, which is why it also
        // had no honest run outcome.
        Assert.Null(StepChangeResolution.Resolve(
            StepChange.AuthorDecides, snippetDeclares: null, stepInput: Input("{}")));
    }

    [Theory]
    [InlineData("""{"changes": "true"}""")]
    [InlineData("""{"changes": 1}""")]
    [InlineData("""{"changes": null}""")]
    public void A_malformed_declaration_is_not_a_declaration(string input)
    {
        // A typo must not be coerced into an answer — that is the failure mode being removed.
        Assert.Null(StepChangeResolution.Resolve(
            StepChange.AuthorDecides, snippetDeclares: null, stepInput: Input(input)));
    }

    [Fact]
    public void The_undeclared_message_names_the_snippet_and_the_node()
    {
        // An author reading this in a failed step has to be able to act on it without
        // reading the source.
        var message = StepChangeResolution.UndeclaredMessage("audit-config", "ssh", "n2");
        Assert.Contains("audit-config", message);
        Assert.Contains("ssh", message);
        Assert.Contains("n2", message);
        Assert.Contains("config_overrides", message);
    }

    // ── what the run reports ────────────────────────────────────────────────

    private static StepOutcome Step(
        string id, bool failed = false, bool changed = false,
        IdempotencyKind tier = IdempotencyKind.RequiresCompensation) =>
        new(id, failed, changed, tier);

    [Fact]
    public void A_run_where_nothing_failed_is_completed_and_has_no_plan()
    {
        var outcome = RunOutcomeCalculator.Compute([Step("a", changed: true), Step("b")]);

        Assert.Equal("completed", outcome.FinalState);
        Assert.Equal(1, outcome.ChangedCount);
        // A run that succeeded has nothing to undo, however much it changed.
        Assert.Empty(outcome.RollbackPlan);
    }

    [Fact]
    public void A_failed_run_whose_changes_are_all_reversible_is_rolled_back()
    {
        var outcome = RunOutcomeCalculator.Compute([
            Step("a", changed: true, tier: IdempotencyKind.Idempotent),
            Step("b", changed: true),
            Step("c", failed: true),
        ]);

        Assert.Equal("rolled_back", outcome.FinalState);
        // Reverse execution order: undoing runs backwards through what was done.
        Assert.Equal(["b", "a"], outcome.RollbackPlan);
    }

    [Fact]
    public void One_irreversible_change_makes_a_failed_run_failed()
    {
        // The distinction the whole model exists for. A run that sent an email and then
        // failed cannot claim everything it did could be undone.
        var outcome = RunOutcomeCalculator.Compute([
            Step("a", changed: true, tier: IdempotencyKind.Idempotent),
            Step("email", changed: true, tier: IdempotencyKind.NonReversible),
            Step("c", failed: true),
        ]);

        Assert.Equal("failed", outcome.FinalState);
        Assert.Equal(2, outcome.ChangedCount);
        // The irreversible node is not IN the plan — nothing can undo it — but the
        // reversible one still is.
        Assert.Equal(["a"], outcome.RollbackPlan);
    }

    [Fact]
    public void A_step_that_ran_without_changing_anything_stays_out_of_the_plan()
    {
        // The case that was unanswerable before the change signal: `a` succeeded, so the old
        // model had no way to tell it apart from a step that wrote something.
        var outcome = RunOutcomeCalculator.Compute([Step("a"), Step("b", failed: true)]);

        Assert.Equal("rolled_back", outcome.FinalState);
        Assert.Equal(0, outcome.ChangedCount);
        Assert.Empty(outcome.RollbackPlan);
    }

    [Fact]
    public void A_fanned_out_node_appears_once_in_the_plan()
    {
        // A `per_device` node produces one step_run per device. Naming it once per device
        // would read as several separate reversals of separate things.
        var outcome = RunOutcomeCalculator.Compute([
            Step("fan", changed: true),
            Step("fan", changed: true),
            Step("fan", changed: true),
            Step("z", failed: true),
        ]);

        Assert.Equal(["fan"], outcome.RollbackPlan);
        // The COUNT still counts every device: three things were changed.
        Assert.Equal(3, outcome.ChangedCount);
    }

    [Fact]
    public void Only_non_reversible_blocks_a_rollback()
    {
        Assert.True(RunOutcomeCalculator.Reversible(IdempotencyKind.Idempotent));
        Assert.True(RunOutcomeCalculator.Reversible(IdempotencyKind.RequiresCompensation));
        Assert.False(RunOutcomeCalculator.Reversible(IdempotencyKind.NonReversible));
    }
}
