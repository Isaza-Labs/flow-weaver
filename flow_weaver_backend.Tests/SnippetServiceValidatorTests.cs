using System.Reflection;
using flow_weaver_backend.Services.Snippet;

namespace flow_weaver_backend.Tests;

// The two content gates SnippetService runs before persisting a snippet: the
// scaffold detector (which exists because agent-authored placeholders once
// shipped as green steps producing fiction) and the Mermaid diagram check
// (opaque code needs a diagram, and a bad paste must fail here rather than
// render garbage in the UI). Reached by reflection — private statics.
public class SnippetServiceValidatorTests
{
    private static readonly Type Service = typeof(SnippetService);

    private static MethodInfo Method(string name)
        => Service.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    // ─── DetectScaffoldCode ─────────────────────────────────────────────

    private static string? DetectScaffold(string? type, string? code)
        => (string?)Method("DetectScaffoldCode").Invoke(null, new object?[] { type, code });

    [Theory]
    [InlineData("scaffold snippet")]
    [InlineData("placeholder snippet")]
    [InlineData("TODO: implement")]
    [InlineData("FIXME: implement")]
    [InlineData("not yet implemented")]
    [InlineData("replace this placeholder")]
    public void Scaffold_FlagsEveryKnownMarker(string marker)
    {
        var error = DetectScaffold("python_snippet", $"def run(inp):\n    # {marker}\n    return {{}}");

        Assert.NotNull(error);
        // The message quotes the marker as it appears in the detector's list
        // (lowercase), not with the casing the author used.
        Assert.Contains(marker, error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("implement the real logic", error);
    }

    [Fact]
    public void Scaffold_MatchingIsCaseInsensitive()
        => Assert.NotNull(DetectScaffold("python_snippet", "# ToDo: ImPlEmEnT this"));

    // Only script-bearing types carry code worth scanning.
    [Theory]
    [InlineData("python_snippet")]
    [InlineData("python")]
    [InlineData("transform")]
    [InlineData("jmespath")]
    [InlineData("ansible_playbook")]
    public void Scaffold_ScansEveryScriptType(string type)
        => Assert.NotNull(DetectScaffold(type, "# scaffold snippet"));

    [Theory]
    [InlineData("ssh")]
    [InlineData("rest_call")]
    [InlineData("integration_action")]
    [InlineData("report")]
    [InlineData("ping")]
    public void Scaffold_IgnoresNonScriptTypes(string type)
        => Assert.Null(DetectScaffold(type, "# scaffold snippet"));

    // The escape hatch: a script that genuinely does work is allowed to
    // mention "placeholder" in a log line.
    [Theory]
    [InlineData("integration(")]
    [InlineData("subprocess")]
    [InlineData("socket.")]
    public void Scaffold_RealWorkOverridesTheMarker(string realWorkToken)
    {
        var code = $"# TODO: implement nicer output\nresult = {realWorkToken}...)";

        Assert.Null(DetectScaffold("python_snippet", code));
    }

    // ...but the escape hatch is exact: `SUBPROCESS` matches (case-insensitive)
    // while `integration(` must match verbatim, since it's a call shape.
    [Fact]
    public void Scaffold_IntegrationEscapeHatchIsCaseSensitive()
    {
        Assert.NotNull(DetectScaffold("python_snippet", "# scaffold snippet\nINTEGRATION(x)"));
        Assert.Null(DetectScaffold("python_snippet", "# scaffold snippet\nintegration(x)"));
    }

    [Theory]
    [InlineData(null, "# scaffold snippet")]
    [InlineData("   ", "# scaffold snippet")]
    [InlineData("python_snippet", null)]
    [InlineData("python_snippet", "")]
    [InlineData("python_snippet", "   ")]
    public void Scaffold_MissingInputsAreNotFlagged(string? type, string? code)
        => Assert.Null(DetectScaffold(type, code));

    [Fact]
    public void Scaffold_CleanCodePasses()
        => Assert.Null(DetectScaffold("python_snippet", "def run(inp):\n    return {'ok': True}"));

    // ─── ValidateLogicDiagram ───────────────────────────────────────────

    private static string? ValidateDiagram(string? type, string? diagram)
        => (string?)Method("ValidateLogicDiagram").Invoke(null, new object?[] { type, diagram });

    // Types whose behaviour lives entirely in user code are opaque without a
    // diagram, so it is mandatory.
    [Theory]
    [InlineData("python_snippet")]
    [InlineData("python")]
    [InlineData("transform")]
    [InlineData("jmespath")]
    public void Diagram_IsRequiredForOpaqueTypes(string type)
    {
        var error = ValidateDiagram(type, null);

        Assert.NotNull(error);
        Assert.Contains("is required", error);
        Assert.Contains(type, error);
    }

    // Built-in types carry their semantics in the handler, so no diagram is
    // needed.
    [Theory]
    [InlineData("ssh")]
    [InlineData("rest_call")]
    [InlineData("integration_action")]
    [InlineData("report")]
    [InlineData("ansible_playbook")]
    [InlineData(null)]
    public void Diagram_IsOptionalForBuiltinTypes(string? type)
        => Assert.Null(ValidateDiagram(type, null));

    [Theory]
    [InlineData("graph TD\n  A-->B")]
    [InlineData("flowchart LR\n  A-->B")]
    [InlineData("sequenceDiagram\n  A->>B: hi")]
    [InlineData("stateDiagram-v2")]
    [InlineData("classDiagram")]
    [InlineData("erDiagram")]
    [InlineData("gantt")]
    [InlineData("journey")]
    [InlineData("gitGraph")]
    [InlineData("pie")]
    [InlineData("mindmap")]
    [InlineData("timeline")]
    [InlineData("quadrantChart")]
    [InlineData("requirementDiagram")]
    public void Diagram_AcceptsEveryKnownDirective(string diagram)
        => Assert.Null(ValidateDiagram("python_snippet", diagram));

    [Fact]
    public void Diagram_DirectiveMatchingIsCaseInsensitive()
        => Assert.Null(ValidateDiagram("python_snippet", "GRAPH TD\n  A-->B"));

    // Leading blank lines and Mermaid comments are skipped when looking for
    // the directive.
    [Fact]
    public void Diagram_SkipsBlankLinesAndComments()
        => Assert.Null(ValidateDiagram("python_snippet", "\n\n%% a comment\n%% another\ngraph TD\n  A-->B"));

    // The common failure: the agent pastes code or prose instead of a diagram.
    [Theory]
    [InlineData("def run(inp):\n    return {}")]
    [InlineData("This workflow pings the devices and reports.")]
    [InlineData("A-->B")]
    public void Diagram_RejectsNonDiagramContent(string diagram)
    {
        var error = ValidateDiagram("python_snippet", diagram);

        Assert.NotNull(error);
        Assert.Contains("must start with a Mermaid directive", error);
    }

    // The error quotes the offending line so the author sees what was wrong.
    [Fact]
    public void Diagram_ErrorQuotesTheOffendingFirstLine()
    {
        var error = ValidateDiagram("python_snippet", "def run(inp):\n    pass");

        Assert.Contains("def run(inp):", error);
    }

    [Fact]
    public void Diagram_LongOffendingLineIsTruncated()
    {
        var error = ValidateDiagram("python_snippet", new string('x', 200));

        Assert.Contains("…", error);
        Assert.DoesNotContain(new string('x', 100), error);
    }

    // Content that is only comments has no directive to find.
    [Fact]
    public void Diagram_CommentOnlyContentIsRejected()
    {
        var error = ValidateDiagram("python_snippet", "%% just a comment\n%% and another");

        Assert.Contains("no non-blank content", error);
    }

    // A bad paste on a NON-required type still trips, so garbage never renders.
    [Fact]
    public void Diagram_BadPasteOnAnOptionalTypeStillFails()
    {
        Assert.NotNull(ValidateDiagram("ssh", "not a diagram at all"));
    }

    // ─── NormalizeIdempotency ───────────────────────────────────────────

    private static string? NormalizeIdempotency(string? raw)
        => (string?)Method("NormalizeIdempotency").Invoke(null, new object?[] { raw });

    [Theory]
    [InlineData("Idempotent", "idempotent")]
    [InlineData("  REQUIRES_COMPENSATION  ", "requires_compensation")]
    [InlineData("non_reversible", "non_reversible")]
    public void Idempotency_IsTrimmedAndLowercased(string raw, string expected)
        => Assert.Equal(expected, NormalizeIdempotency(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Idempotency_BlankBecomesNull(string? raw)
        => Assert.Null(NormalizeIdempotency(raw));

    // ─── TruncatePreview ────────────────────────────────────────────────

    private static string TruncatePreview(string s, int max)
        => (string)Method("TruncatePreview").Invoke(null, new object?[] { s, max })!;

    [Fact]
    public void Truncate_ShortStringsAreUnchanged()
        => Assert.Equal("short", TruncatePreview("short", 10));

    [Fact]
    public void Truncate_ExactLengthIsUnchanged()
        => Assert.Equal("12345", TruncatePreview("12345", 5));

    [Fact]
    public void Truncate_LongStringsGetAnEllipsis()
        => Assert.Equal("123…", TruncatePreview("123456", 3));
}
