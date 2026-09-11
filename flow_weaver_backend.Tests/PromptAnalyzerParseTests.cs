using flow_weaver_backend.Services.Ai.Constructor;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Traceability: FR-002 (TC-FW-002) — prompt-sufficiency evaluation.
// The LLM call itself is non-deterministic and lives behind IAiProvider, so
// PromptAnalyzer.Parse is the traceable seam: it turns a raw JSON verdict into
// a PromptAnalysis and owns the contract-relevant behavior — fence tolerance,
// self-consistency coercion, fail-OPEN on unparseable content, and fail-CLOSED
// on valid-but-incomplete objects.
//
// Parse only touches the logger, so the repository/identity/factory constructor
// deps are passed as null — building them would add nothing to what Parse does.
public class PromptAnalyzerParseTests
{
    private static PromptAnalyzer Analyzer() =>
        new(null!, null!, null!, NullLogger<PromptAnalyzer>.Instance);

    [Fact]
    public void Sufficient_prompt_yields_true_with_no_questions()
    {
        var r = Analyzer().Parse(
            """{"sufficient": true, "missing_aspects": [], "clarifying_questions": []}""");
        Assert.True(r.Sufficient);
        Assert.Empty(r.MissingAspects);
        Assert.Empty(r.ClarifyingQuestions);
    }

    [Fact]
    public void Insufficient_prompt_surfaces_missing_and_questions()
    {
        var r = Analyzer().Parse(
            """{"sufficient": false, "missing_aspects": ["target devices", "schedule"], "clarifying_questions": ["Which devices?", "When should it run?"]}""");
        Assert.False(r.Sufficient);
        Assert.Equal(new[] { "target devices", "schedule" }, r.MissingAspects);
        Assert.Equal(new[] { "Which devices?", "When should it run?" }, r.ClarifyingQuestions);
    }

    [Fact]
    public void Tolerates_a_json_code_fence()
    {
        // Providers emit ```json ... ``` even when told not to.
        var r = Analyzer().Parse(
            "```json\n{\"sufficient\": true, \"missing_aspects\": [], \"clarifying_questions\": []}\n```");
        Assert.True(r.Sufficient);
    }

    [Fact]
    public void Sufficient_true_but_questions_present_is_coerced_to_insufficient()
    {
        // Self-consistency guardrail: operators prefer one extra clarification
        // over a false pass, so the questions win over the boolean.
        var r = Analyzer().Parse(
            """{"sufficient": true, "clarifying_questions": ["Which site?"]}""");
        Assert.False(r.Sufficient);
        Assert.Equal(new[] { "Which site?" }, r.ClarifyingQuestions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not json at all")]
    [InlineData("{ broken json ")]
    public void Garbage_or_empty_degrades_to_sufficient_true(string content)
    {
        // Fail-open: a parse failure must not block the user. Downstream
        // structural tools still catch real gaps.
        Assert.True(Analyzer().Parse(content).Sufficient);
    }

    [Fact]
    public void Valid_json_missing_the_flag_fails_closed_to_insufficient()
    {
        // Distinct from garbage above: this JSON parsed fine but never affirmed
        // sufficiency, so the analyzer does NOT pass it through.
        var r = Analyzer().Parse("""{"note": "no verdict here"}""");
        Assert.False(r.Sufficient);
    }

    [Fact]
    public void Non_string_and_blank_list_items_are_filtered()
    {
        var r = Analyzer().Parse(
            """{"sufficient": false, "clarifying_questions": ["keep", 123, null, "  ", "also"]}""");
        Assert.Equal(new[] { "keep", "also" }, r.ClarifyingQuestions);
    }

    [Fact]
    public void Only_a_json_boolean_true_counts_as_sufficient()
    {
        // The string "true" must not be read as a sufficiency verdict.
        var r = Analyzer().Parse("""{"sufficient": "true", "clarifying_questions": []}""");
        Assert.False(r.Sufficient);
    }

    [Fact]
    public async Task Empty_prompt_short_circuits_without_touching_a_provider()
    {
        // AnalyzeAsync must return sufficient for a blank prompt without ever
        // reaching the provider repository (which is null here).
        var r = await Analyzer().AnalyzeAsync("   ", CancellationToken.None);
        Assert.True(r.Sufficient);
    }
}
