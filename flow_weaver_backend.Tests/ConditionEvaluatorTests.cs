using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Each test targets one rule of the edge-condition grammar so a failure
// message points at the exact broken behavior. The focus is (1) template
// resolution parity with VariableResolver (hyphenated node ids, array
// indexing, per-device envelopes) and (2) the boolean algebra on top of
// the resolved strings (comparisons, truthiness, short-circuiting).
public class ConditionEvaluatorTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    private static ConditionEvaluator NewEvaluator() =>
        new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance);

    private static Dictionary<string, StepResult> Steps(params (string id, string json)[] entries)
    {
        var dict = new Dictionary<string, StepResult>();
        foreach (var (id, json) in entries)
            dict[id] = new StepResult(E(json));
        return dict;
    }

    [Fact]
    public void Numeric_equality_passes()
    {
        var eval = NewEvaluator();
        var steps = Steps(("rest", """{ "status_code": 200 }"""));
        Assert.True(eval.Evaluate("{{ steps.rest.output.status_code }} == 200", steps));
    }

    [Fact]
    public void Numeric_range_with_and_operator()
    {
        var eval = NewEvaluator();
        var steps = Steps(("rest", """{ "status_code": 201 }"""));
        var expr = "{{ steps.rest.output.status_code }} >= 200 && {{ steps.rest.output.status_code }} < 300";
        Assert.True(eval.Evaluate(expr, steps));
    }

    [Fact]
    public void Or_short_circuits_on_first_true()
    {
        var eval = NewEvaluator();
        var steps = Steps(("rest", """{ "status_code": 404 }"""));
        var expr = "{{ steps.rest.output.status_code }} == 200 || {{ steps.rest.output.status_code }} == 404";
        Assert.True(eval.Evaluate(expr, steps));
    }

    [Fact]
    public void Boolean_truthy_without_operator()
    {
        var eval = NewEvaluator();
        var steps = Steps(("ping", """{ "success": true }"""));
        Assert.True(eval.Evaluate("{{ steps.ping.output.success }}", steps));
    }

    [Fact]
    public void Boolean_false_without_operator()
    {
        var eval = NewEvaluator();
        var steps = Steps(("ping", """{ "success": false }"""));
        Assert.False(eval.Evaluate("{{ steps.ping.output.success }}", steps));
    }

    [Fact]
    public void String_equality_with_quoted_literal()
    {
        var eval = NewEvaluator();
        var steps = Steps(("step", """{ "status": "completed" }"""));
        Assert.True(eval.Evaluate("{{ steps.step.output.status }} == \"completed\"", steps));
    }

    [Fact]
    public void Hyphenated_node_id_resolves_in_condition()
    {
        // Regression: the pre-fix regex used \w+ which rejected hyphens,
        // leaving `{{ steps.ssh-lldp.output.exit_code }}` literal and
        // turning every condition on a hyphenated node into false.
        var eval = NewEvaluator();
        var steps = Steps(("ssh-lldp", """{ "exit_code": 0 }"""));
        Assert.True(eval.Evaluate("{{ steps.ssh-lldp.output.exit_code }} == 0", steps));
    }

    [Fact]
    public void Array_index_in_path_resolves_in_condition()
    {
        var eval = NewEvaluator();
        var steps = Steps(("sync", """{ "updated": [ {"ok": true}, {"ok": false} ] }"""));
        Assert.True(eval.Evaluate("{{ steps.sync.output.updated[0].ok }} == true", steps));
        Assert.False(eval.Evaluate("{{ steps.sync.output.updated[1].ok }} == true", steps));
    }

    [Fact]
    public void Per_device_envelope_is_navigable_via_index()
    {
        // Mirrors WorkflowExecutor.AggregatePerDeviceOutput's shape.
        // Without array indexing an edge condition could not inspect a
        // specific device's result of a fan-out upstream node.
        var eval = NewEvaluator();
        var steps = Steps(("ssh", """
            {
              "devices": [
                { "device_id": "a", "status": "completed", "output": { "exit_code": 0 } },
                { "device_id": "b", "status": "failed",    "output": { "exit_code": 1 } }
              ]
            }
            """));
        Assert.True(eval.Evaluate("{{ steps.ssh.output.devices[0].output.exit_code }} == 0", steps));
        Assert.True(eval.Evaluate("{{ steps.ssh.output.devices[1].output.exit_code }} != 0", steps));
    }

    [Fact]
    public void Missing_step_keeps_literal_and_evaluates_false()
    {
        // Template stays literal → comparison becomes
        // "{{ steps.missing.output.x }} == 1" which is neither a
        // matching number nor a matching string → false.
        var eval = NewEvaluator();
        Assert.False(eval.Evaluate("{{ steps.missing.output.x }} == 1", Steps()));
    }

    [Fact]
    public void Empty_expression_is_false()
    {
        var eval = NewEvaluator();
        Assert.False(eval.Evaluate("   ", Steps()));
    }

    [Fact]
    public void Inequality_operator_works()
    {
        var eval = NewEvaluator();
        var steps = Steps(("transform", """{ "count": 5 }"""));
        Assert.True(eval.Evaluate("{{ steps.transform.output.count }} != 0", steps));
    }

    [Fact]
    public void Device_template_resolves_when_context_provided()
    {
        var eval = NewEvaluator();
        var deviceCtx = E("""{ "role": "core", "site": "dc-1" }""");
        Assert.True(eval.Evaluate("{{ device.role }} == \"core\"", Steps(), deviceCtx));
        Assert.False(eval.Evaluate("{{ device.role }} == \"edge\"", Steps(), deviceCtx));
    }

    [Fact]
    public void Device_template_without_context_is_not_substituted()
    {
        // No context → template stays literal → the comparison's LHS is
        // the raw `{{ device.role }}` string which never equals "core".
        // This is the documented safe-default: missing context never
        // silently flips a condition to true.
        var eval = NewEvaluator();
        Assert.False(eval.Evaluate("{{ device.role }} == \"core\"", Steps(), deviceContext: null));
    }

    [Fact]
    public void Device_and_step_templates_combine_in_condition()
    {
        var eval = NewEvaluator();
        var deviceCtx = E("""{ "role": "core" }""");
        var steps = Steps(("ping", """{ "success": true }"""));
        var expr = "{{ device.role }} == \"core\" && {{ steps.ping.output.success }} == true";
        Assert.True(eval.Evaluate(expr, steps, deviceCtx));
    }
    // ─── input / run namespaces (workflow.v1 SPEC §9) ───────────────────
    // Conditions resolve `steps`, `input` and `run`. Branching on the run's
    // own input is the case the namespace exists for: one definition serves
    // a dry run and a real one.

    [Fact]
    public void Input_template_resolves_true_in_condition()
    {
        var eval = NewEvaluator();
        var input = E("""{ "mode": "y", "apply": true }""");
        Assert.True(eval.Evaluate("{{ input.mode }} == 'y'", Steps(), runInput: input));
        Assert.True(eval.Evaluate("{{ input.apply }} == true", Steps(), runInput: input));
    }

    [Fact]
    public void Input_template_resolves_false_when_the_value_differs()
    {
        var eval = NewEvaluator();
        var input = E("""{ "mode": "n", "apply": false }""");
        Assert.False(eval.Evaluate("{{ input.mode }} == 'y'", Steps(), runInput: input));
        Assert.False(eval.Evaluate("{{ input.apply }} == true", Steps(), runInput: input));
    }

    [Fact]
    public void Input_template_without_a_run_input_is_false()
    {
        // Fail closed: no input context → the reference stays literal → the
        // comparison is false, never accidentally true.
        var eval = NewEvaluator();
        Assert.False(eval.Evaluate("{{ input.apply }} == true", Steps()));
    }

    [Fact]
    public void Run_template_resolves_in_condition()
    {
        var eval = NewEvaluator();
        var run = E("""{ "environment": "production", "trigger": "schedule" }""");
        Assert.True(eval.Evaluate("{{ run.environment }} == 'production'", Steps(), runContext: run));
        Assert.False(eval.Evaluate("{{ run.environment }} == 'qa'", Steps(), runContext: run));
    }

    [Fact]
    public void Input_run_and_steps_combine_in_one_condition()
    {
        var eval = NewEvaluator();
        var steps = Steps(("ping", """{ "success": true }"""));
        var input = E("""{ "apply": true }""");
        var run = E("""{ "environment": "production" }""");
        var expr = "{{ input.apply }} == true && {{ run.environment }} == 'production'"
                   + " && {{ steps.ping.output.success }} == true";
        Assert.True(eval.Evaluate(expr, steps, deviceContext: null, runInput: input, runContext: run));
    }

    [Fact]
    public void Filters_apply_inside_condition_references()
    {
        // The evaluator shares VariableResolver's filter chain, so a filter
        // means the same thing in an edge as it does in a payload.
        var eval = NewEvaluator();
        var input = E("""{ "site": "  dc-1  " }""");
        var run = E("""{ "environment": "production" }""");
        var steps = Steps(("probe", """{ "error": "" }"""));

        Assert.True(eval.Evaluate("{{ input.site | trim }} == 'dc-1'", Steps(), runInput: input));
        Assert.True(eval.Evaluate("{{ run.environment | upper }} == 'PRODUCTION'", Steps(), runContext: run));
        // `default` is the only filter that recovers an absent reference —
        // and an empty string counts as absent (SPEC §4).
        Assert.True(eval.Evaluate("{{ steps.probe.output.error | default('none') }} == 'none'", steps));
        Assert.True(eval.Evaluate("{{ input.missing | default('n/a') | upper }} == 'N/A'", Steps(), runInput: input));
    }

    [Fact]
    public void Device_template_never_resolves_at_the_orchestrator_call_site()
    {
        // SPEC §9: `{{ device.* }}` does NOT resolve in a condition. A
        // conditional edge fires once at the DAG level after its source node
        // completes, so there is no single current device even when the
        // source fanned out — the orchestrator therefore passes a null
        // device context and the reference stays literal, i.e. false.
        var eval = NewEvaluator();
        var input = E("""{ "apply": true }""");
        var run = E("""{ "environment": "production" }""");
        Assert.False(eval.Evaluate(
            "{{ device.name }} == 'r1'", Steps(),
            deviceContext: null, runInput: input, runContext: run));
    }

    [Fact]
    public void Unresolved_comparison_fails_closed_even_with_contexts_present()
    {
        var eval = NewEvaluator();
        var input = E("""{ "apply": true }""");
        var run = E("""{ "environment": "production" }""");
        // Paths that don't exist in the supplied contexts stay literal.
        Assert.False(eval.Evaluate("{{ input.nope }} == true", Steps(), runInput: input));
        Assert.False(eval.Evaluate("{{ run.nope }} == 'production'", Steps(), runContext: run));
        Assert.False(eval.Evaluate(
            "{{ input.apply }} == true && {{ run.nope }} == 'x'", Steps(),
            deviceContext: null, runInput: input, runContext: run));
    }

    [Fact]
    public void Input_word_boundary_does_not_swallow_similar_names()
    {
        // Mirrors VariableResolver's `\b` anchor: `{{ inputs }}` /
        // `{{ runtime }}` are not the `input` / `run` namespaces.
        var eval = NewEvaluator();
        var input = E("""{ "apply": true }""");
        var run = E("""{ "environment": "production" }""");
        Assert.False(eval.Evaluate("{{ inputs.apply }} == true", Steps(), runInput: input));
        Assert.False(eval.Evaluate("{{ runtime }} == 'production'", Steps(), runContext: run));
    }
}
