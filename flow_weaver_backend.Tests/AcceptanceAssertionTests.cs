using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.Engine;
using StepOutputRow = flow_weaver_backend.Services.Ai.Tools.Handlers.RunAcceptanceTestsHandler.StepOutputRow;

namespace flow_weaver_backend.Tests;

// The acceptance-test assertion engine. This is what tells the agent "your
// workflow works" — a false PASS is the worst outcome here, because the agent
// then promotes a broken workflow with confidence. Every assertion kind is
// exercised, plus the run-matching rule that decides WHICH run gets graded.
public class AcceptanceAssertionTests
{
    private static StepOutputRow Step(
        string nodeId, string status = StepStatus.Completed, string? output = null)
        => new(Guid.NewGuid(), nodeId, status,
            output is null ? default : TestJson.Element(output));

    private static List<object> Evaluate(
        string assertions, string runStatus = "completed", params StepOutputRow[] steps)
        => RunAcceptanceTestsHandler.EvaluateAssertions(
            TestJson.Element(assertions), runStatus, steps);

    // The failure objects are anonymous types; comparing their JSON is the
    // stable way to assert on them.
    private static JsonElement AsJson(object failure)
        => JsonSerializer.SerializeToElement(failure);

    // ─── status_equals ──────────────────────────────────────────────────

    [Fact]
    public void StatusEquals_PassesOnAMatch()
    {
        Assert.Empty(Evaluate("""[{"kind":"status_equals","expected":"completed"}]""", "completed"));
    }

    [Fact]
    public void StatusEquals_FailsOnAMismatchAndReportsBothSides()
    {
        var failures = Evaluate("""[{"kind":"status_equals","expected":"completed"}]""", "failed");

        var failure = AsJson(Assert.Single(failures));
        Assert.Equal("completed", failure.GetProperty("expected").GetString());
        Assert.Equal("failed", failure.GetProperty("actual").GetString());
    }

    // Status comparison is ordinal — "Completed" is not "completed". A looser
    // match would let a differently-cased status pass silently.
    [Fact]
    public void StatusEquals_IsCaseSensitive()
    {
        Assert.Single(Evaluate("""[{"kind":"status_equals","expected":"Completed"}]""", "completed"));
    }

    // An assertion with no `expected` cannot pass — otherwise a malformed test
    // would report green.
    [Fact]
    public void StatusEquals_WithoutAnExpectedValueFails()
    {
        Assert.Single(Evaluate("""[{"kind":"status_equals"}]""", "completed"));
    }

    // ─── step_succeeded / step_failed ───────────────────────────────────

    [Fact]
    public void StepSucceeded_PassesWhenTheStepCompleted()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_succeeded","path":"n1"}]""", "completed",
            Step("n1", StepStatus.Completed)));
    }

    [Fact]
    public void StepSucceeded_FailsWhenTheStepDidNot()
    {
        var failures = Evaluate(
            """[{"kind":"step_succeeded","path":"n1"}]""", "failed",
            Step("n1", StepStatus.Failed));

        var failure = AsJson(Assert.Single(failures));
        Assert.Equal(StepStatus.Completed, failure.GetProperty("expected").GetString());
        Assert.Equal(StepStatus.Failed, failure.GetProperty("actual").GetString());
    }

    // The negative assertion: "this step is expected to fail" (error-path
    // tests) must pass when it does.
    [Fact]
    public void StepFailed_PassesWhenTheStepFailed()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_failed","path":"n1"}]""", "failed",
            Step("n1", StepStatus.Failed)));
    }

    [Fact]
    public void StepFailed_FailsWhenTheStepSucceeded()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_failed","path":"n1"}]""", "completed",
            Step("n1", StepStatus.Completed)));
    }

    // A missing step must FAIL, not silently pass — this is the false-green
    // case that matters most.
    [Fact]
    public void StepAssertion_MissingStepIsAFailureNotAPass()
    {
        var failures = Evaluate(
            """[{"kind":"step_succeeded","path":"ghost"}]""", "completed",
            Step("n1"));

        Assert.Equal("step_not_found", AsJson(Assert.Single(failures)).GetProperty("reason").GetString());
    }

    [Fact]
    public void StepAssertion_WithoutAPathFails()
    {
        var failures = Evaluate("""[{"kind":"step_succeeded"}]""");

        Assert.Equal("path_required", AsJson(Assert.Single(failures)).GetProperty("reason").GetString());
    }

    // ─── step_output_equals ─────────────────────────────────────────────

    [Fact]
    public void OutputEquals_PassesOnAnExactNestedMatch()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_output_equals","path":"n1.result.code","expected":0}]""",
            "completed",
            Step("n1", output: """{"result":{"code":0}}""")));
    }

    [Fact]
    public void OutputEquals_MatchesTheWholeOutputWhenThePathIsJustTheNode()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_output_equals","path":"n1","expected":{"ok":true}}]""",
            "completed",
            Step("n1", output: """{"ok":true}""")));
    }

    [Fact]
    public void OutputEquals_FailsOnAValueMismatch()
    {
        var failures = Evaluate(
            """[{"kind":"step_output_equals","path":"n1.code","expected":0}]""",
            "completed",
            Step("n1", output: """{"code":1}"""));

        var failure = AsJson(Assert.Single(failures));
        Assert.Equal(0, failure.GetProperty("expected").GetInt32());
    }

    // Comparison is by raw JSON, so 0 and "0" are different — a looser
    // comparison would mask a type regression in a handler's output.
    [Fact]
    public void OutputEquals_DistinguishesTypes()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_output_equals","path":"n1.code","expected":"0"}]""",
            "completed",
            Step("n1", output: """{"code":0}""")));
    }

    // A path that misses must fail rather than compare `undefined` to
    // `undefined` and pass.
    [Fact]
    public void OutputEquals_MissingPathFails()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_output_equals","path":"n1.nope.deeper","expected":1}]""",
            "completed",
            Step("n1", output: """{"code":0}""")));
    }

    [Fact]
    public void OutputEquals_MissingStepFails()
    {
        var failures = Evaluate(
            """[{"kind":"step_output_equals","path":"ghost.code","expected":0}]""",
            "completed",
            Step("n1", output: "{}"));

        Assert.Equal("step_not_found", AsJson(Assert.Single(failures)).GetProperty("reason").GetString());
    }

    // ─── step_output_contains ───────────────────────────────────────────

    [Fact]
    public void OutputContains_PassesOnASubstringOfAStringValue()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_output_contains","path":"n1.stdout","expected":"IOS"}]""",
            "completed",
            Step("n1", output: """{"stdout":"Cisco IOS 15.2"}""")));
    }

    [Fact]
    public void OutputContains_FailsWhenTheSubstringIsAbsent()
    {
        var failures = Evaluate(
            """[{"kind":"step_output_contains","path":"n1.stdout","expected":"NXOS"}]""",
            "completed",
            Step("n1", output: """{"stdout":"Cisco IOS 15.2"}"""));

        var failure = AsJson(Assert.Single(failures));
        Assert.Equal("NXOS", failure.GetProperty("expected").GetString());
        Assert.Contains("IOS", failure.GetProperty("actual").GetString());
    }

    // A non-string target is searched as raw JSON, so structural checks work
    // too.
    [Fact]
    public void OutputContains_SearchesRawJsonForNonStringTargets()
    {
        Assert.Empty(Evaluate(
            """[{"kind":"step_output_contains","path":"n1.devices","expected":"rtr-1"}]""",
            "completed",
            Step("n1", output: """{"devices":["rtr-1","rtr-2"]}""")));
    }

    [Fact]
    public void OutputContains_IsCaseSensitive()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_output_contains","path":"n1.stdout","expected":"ios"}]""",
            "completed",
            Step("n1", output: """{"stdout":"Cisco IOS"}""")));
    }

    [Fact]
    public void OutputContains_WithoutAnExpectedValueFails()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_output_contains","path":"n1.stdout"}]""",
            "completed",
            Step("n1", output: """{"stdout":"anything"}""")));
    }

    [Fact]
    public void OutputContains_MissingStepFails()
    {
        Assert.Single(Evaluate(
            """[{"kind":"step_output_contains","path":"ghost.x","expected":"y"}]""",
            "completed",
            Step("n1", output: "{}")));
    }

    // ─── unknown / malformed assertions ─────────────────────────────────

    // An unrecognised kind must fail loudly: a typo'd assertion that silently
    // passes is exactly how a broken workflow gets promoted.
    [Theory]
    [InlineData("step_output_matches")]
    [InlineData("status_is")]
    [InlineData("")]
    public void UnknownAssertionKindFails(string kind)
    {
        var failures = Evaluate($$"""[{"kind":"{{kind}}","path":"n1"}]""");

        Assert.Equal("unknown_assertion_kind", AsJson(Assert.Single(failures)).GetProperty("reason").GetString());
    }

    [Fact]
    public void MissingKindFails()
    {
        Assert.Single(Evaluate("""[{"path":"n1"}]"""));
    }

    // Non-object entries are skipped rather than crashing the whole test run.
    [Fact]
    public void NonObjectEntriesAreSkipped()
    {
        Assert.Empty(Evaluate("""["a string", 42, null]"""));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"not":"an array"}""")]
    public void NonArrayAssertionsYieldNoFailures(string assertions)
    {
        Assert.Empty(Evaluate(assertions));
    }

    [Fact]
    public void EveryFailingAssertionIsReported()
    {
        var failures = Evaluate("""
            [{"kind":"status_equals","expected":"completed"},
             {"kind":"step_succeeded","path":"ghost"},
             {"kind":"nonsense"}]
            """, "failed");

        Assert.Equal(3, failures.Count);
    }

    // ─── NavigatePath ───────────────────────────────────────────────────

    private static JsonElement Navigate(string json, string path)
        => RunAcceptanceTestsHandler.NavigatePath(TestJson.Element(json), path);

    [Fact]
    public void Navigate_WalksNestedObjects()
    {
        Assert.Equal(42, Navigate("""{"a":{"b":{"c":42}}}""", "a.b.c").GetInt32());
    }

    [Fact]
    public void Navigate_EmptySegmentsAreIgnored()
    {
        Assert.Equal(42, Navigate("""{"a":{"b":42}}""", "a..b").GetInt32());
    }

    [Theory]
    [InlineData("""{"a":1}""", "b")]
    [InlineData("""{"a":1}""", "a.b")]     // a is not an object
    [InlineData("""[1,2]""", "a")]          // root is not an object
    public void Navigate_MissesReturnUndefined(string json, string path)
    {
        Assert.Equal(JsonValueKind.Undefined, Navigate(json, path).ValueKind);
    }

    // Array indexing is deliberately unsupported — the assertion docs say so.
    [Fact]
    public void Navigate_DoesNotSupportArrayIndexes()
    {
        Assert.Equal(JsonValueKind.Undefined, Navigate("""{"a":[1,2]}""", "a.0").ValueKind);
    }

    // ─── InputsMatch (which run gets graded) ────────────────────────────

    private static bool InputsMatch(string testInputs, string runInputs)
        => RunAcceptanceTestsHandler.InputsMatch(
            TestJson.Element(testInputs), TestJson.Element(runInputs));

    // Matching is a SUBSET check: the run may carry extra keys the test
    // doesn't pin.
    [Fact]
    public void InputsMatch_RunMayCarryExtraKeys()
    {
        Assert.True(InputsMatch("""{"host":"rtr-1"}""", """{"host":"rtr-1","extra":true}"""));
    }

    [Fact]
    public void InputsMatch_EveryPinnedKeyMustBePresent()
    {
        Assert.False(InputsMatch("""{"host":"rtr-1","vlan":10}""", """{"host":"rtr-1"}"""));
    }

    // False positives would grade against the WRONG run, so the value check is
    // byte-equal raw JSON.
    [Fact]
    public void InputsMatch_ValuesMustBeIdentical()
    {
        Assert.False(InputsMatch("""{"vlan":10}""", """{"vlan":"10"}"""));
        Assert.False(InputsMatch("""{"host":"rtr-1"}""", """{"host":"rtr-2"}"""));
    }

    [Fact]
    public void InputsMatch_NestedValuesAreComparedWhole()
    {
        Assert.True(InputsMatch("""{"cfg":{"a":1}}""", """{"cfg":{"a":1}}"""));
        Assert.False(InputsMatch("""{"cfg":{"a":1}}""", """{"cfg":{"a":2}}"""));
    }

    // A test that pins nothing matches any run.
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InputsMatch_TestWithoutPinnedInputsMatchesAnything(string testInputs)
    {
        Assert.True(InputsMatch(testInputs, """{"anything":1}"""));
    }

    // ...but a test that DOES pin inputs cannot match a run with no inputs.
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void InputsMatch_PinnedTestCannotMatchANonObjectRunInput(string runInputs)
    {
        Assert.False(InputsMatch("""{"host":"rtr-1"}""", runInputs));
    }
}
