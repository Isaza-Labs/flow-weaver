using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Validation;

namespace flow_weaver_backend.Tests;

// The anti-hallucination detectors inside WorkflowReferenceValidator. These
// exist because of real incidents: an agent shipped a report node whose config
// held literal "PENDING_RECONCILIATION" rows, the step ran green, and the user
// received a fiction-filled email. Create time is the only honest gate — at
// runtime there is no way to tell "template not resolved" from "the author
// typed PENDING_". Reached by reflection (private statics).
public class WorkflowReferenceValidatorDetectorTests
{
    private static readonly Type Validator = typeof(WorkflowReferenceValidator);

    private static MethodInfo Method(string name)
        => Validator.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    // ─── DetectPlaceholderSnippetIds ────────────────────────────────────

    private static List<string> DetectPlaceholders(string nodes)
        => (List<string>)Method("DetectPlaceholderSnippetIds")
            .Invoke(null, new object?[] { TestJson.Element(nodes) })!;

    // The agent batches create_snippet + create_workflow and sometimes forgets
    // to chain the returned id. Catching it here beats an opaque
    // `invalid_snippet_id` several steps later.
    [Theory]
    [InlineData("REPLACE_SNIPPET_ID")]
    [InlineData("TODO")]
    [InlineData("my-snippet")]
    public void DetectPlaceholders_FlagsNonGuidSnippetIds(string snippetId)
    {
        var errors = DetectPlaceholders($$"""[{"id":"n1","snippet_id":"{{snippetId}}"}]""");

        var error = Assert.Single(errors);
        Assert.Contains("n1", error);
        Assert.Contains(snippetId, error);
        Assert.Contains("create_snippet", error);
    }

    [Fact]
    public void DetectPlaceholders_AcceptsRealGuids()
    {
        var errors = DetectPlaceholders($$"""[{"id":"n1","snippet_id":"{{Guid.NewGuid()}}"}]""");

        Assert.Empty(errors);
    }

    // The reserved sentinels must stay in sync with DependencyResolver —
    // divergence once failed a 228-node import.
    [Theory]
    [InlineData("__start__")]
    [InlineData("__end__")]
    [InlineData("subflow")]
    [InlineData("integration_action")]
    public void DetectPlaceholders_AcceptsReservedSentinels(string sentinel)
    {
        Assert.Empty(DetectPlaceholders($$"""[{"id":"n1","snippet_id":"{{sentinel}}"}]"""));
    }

    // Sentinels are matched case-sensitively; an odd-cased variant is not a
    // sentinel and must be flagged rather than silently accepted.
    [Fact]
    public void DetectPlaceholders_SentinelMatchingIsCaseSensitive()
    {
        Assert.Single(DetectPlaceholders("""[{"id":"n1","snippet_id":"__START__"}]"""));
    }

    [Theory]
    [InlineData("""[{"id":"n1"}]""")]                        // no snippet_id
    [InlineData("""[{"id":"n1","snippet_id":""}]""")]        // empty
    [InlineData("""[{"id":"n1","snippet_id":"   "}]""")]     // blank
    [InlineData("""[{"id":"n1","snippet_id":123}]""")]       // not a string
    [InlineData("""["not an object"]""")]
    public void DetectPlaceholders_IgnoresAbsentOrNonStringIds(string nodes)
    {
        Assert.Empty(DetectPlaceholders(nodes));
    }

    [Fact]
    public void DetectPlaceholders_ReportsOneErrorPerOffendingNode()
    {
        var errors = DetectPlaceholders("""
            [{"id":"n1","snippet_id":"TODO"},
             {"id":"n2","snippet_id":"ALSO_TODO"}]
            """);

        Assert.Equal(2, errors.Count);
    }

    // A node with no id still yields an actionable error.
    [Fact]
    public void DetectPlaceholders_UnnamedNodeFallsBackToUnknown()
    {
        Assert.Contains("(unknown)", Assert.Single(DetectPlaceholders("""[{"snippet_id":"TODO"}]""")));
    }

    // ─── DetectScaffoldInConfigOverrides ────────────────────────────────

    private static List<string> DetectScaffolds(string nodes)
        => (List<string>)Method("DetectScaffoldInConfigOverrides")
            .Invoke(null, new object?[] { TestJson.Element(nodes) })!;

    // The exact markers from the LLDP/DNS incident and its siblings.
    [Theory]
    [InlineData("PENDING_RECONCILIATION")]
    [InlineData("PENDING_TAGGING")]
    [InlineData("pending_implementation")]
    [InlineData("Scaffold snippet")]
    [InlineData("draft scaffold")]
    [InlineData("placeholder row")]
    [InlineData("TODO: implement")]
    [InlineData("FIXME: implement")]
    [InlineData("not yet implemented")]
    [InlineData("replace this placeholder")]
    public void DetectScaffolds_FlagsEveryKnownMarker(string marker)
    {
        var nodes = "[{\"id\":\"n1\",\"config_overrides\":{\"message\":"
                    + JsonSerializer.Serialize(marker) + "}}]";

        var error = Assert.Single(DetectScaffolds(nodes));
        Assert.Contains("n1", error);
        Assert.Contains("scaffold marker", error);
    }

    [Fact]
    public void DetectScaffolds_MatchingIsCaseInsensitiveAndSubstring()
    {
        var nodes = """[{"id":"n1","config_overrides":{"m":"row: PeNdInG_TaGgInG here"}}]""";

        Assert.Single(DetectScaffolds(nodes));
    }

    // A template referencing a previous step is exactly the pattern we WANT —
    // it must never trip the gate, even if it also contains a marker word.
    [Theory]
    [InlineData("{{ steps.lookup.output.status }}")]
    [InlineData("{{ device.name }}")]
    [InlineData("prefix {{ steps.x.output.pending_tagging }} suffix")]
    public void DetectScaffolds_SkipsTemplateExpressions(string value)
    {
        var nodes = "[{\"id\":\"n1\",\"config_overrides\":{\"m\":"
                    + JsonSerializer.Serialize(value) + "}}]";

        Assert.Empty(DetectScaffolds(nodes));
    }

    // The walk is recursive so a marker buried in a nested table still fires,
    // and the reported path tells the agent exactly which field to fix.
    [Fact]
    public void DetectScaffolds_WalksNestedObjectsAndReportsThePath()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"report":{"sections":[{"title":"draft scaffold"}]}}}]
            """;

        var error = Assert.Single(DetectScaffolds(nodes));
        Assert.Contains(".report.sections[0].title", error);
    }

    [Fact]
    public void DetectScaffolds_WalksArrayElements()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"rows":["ok","PENDING_RECONCILIATION"]}}]
            """;

        Assert.Contains(".rows[1]", Assert.Single(DetectScaffolds(nodes)));
    }

    // Only string VALUES are scanned — a key that happens to be named like a
    // marker is not a scaffold.
    [Fact]
    public void DetectScaffolds_IgnoresKeysAndNonStringValues()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"pending_tagging":true,"count":42,"nothing":null}}]
            """;

        Assert.Empty(DetectScaffolds(nodes));
    }

    [Fact]
    public void DetectScaffolds_CleanConfigProducesNoErrors()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"command":"show version","timeout":30}}]
            """;

        Assert.Empty(DetectScaffolds(nodes));
    }

    // One error per offending string keeps the fix list readable.
    [Fact]
    public void DetectScaffolds_OneErrorPerOffendingString()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"a":"draft scaffold","b":"placeholder row"}}]
            """;

        Assert.Equal(2, DetectScaffolds(nodes).Count);
    }

    // A single string carrying two markers still reports once.
    [Fact]
    public void DetectScaffolds_MultipleMarkersInOneStringReportOnce()
    {
        var nodes = """
            [{"id":"n1","config_overrides":{"a":"draft scaffold and placeholder row"}}]
            """;

        Assert.Single(DetectScaffolds(nodes));
    }

    [Theory]
    [InlineData("""[{"id":"n1"}]""")]                                  // no config_overrides
    [InlineData("""[{"id":"n1","config_overrides":"not an object"}]""")]
    [InlineData("""["not an object"]""")]
    public void DetectScaffolds_IgnoresNodesWithoutUsableConfig(string nodes)
    {
        Assert.Empty(DetectScaffolds(nodes));
    }

    // ─── TryReadGuid ────────────────────────────────────────────────────

    private static (bool Ok, Guid Value, string Error) TryReadGuid(string json, string key)
    {
        var args = new object?[] { TestJson.Element(json), key, null, null };
        var ok = (bool)Method("TryReadGuid").Invoke(null, args)!;
        return (ok, (Guid)args[2]!, (string)args[3]!);
    }

    [Fact]
    public void TryReadGuid_ReadsAValidGuid()
    {
        var id = Guid.NewGuid();

        var (ok, value, error) = TryReadGuid($$"""{"integration_id":"{{id}}"}""", "integration_id");

        Assert.True(ok);
        Assert.Equal(id, value);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"integration_id":null}""")]
    public void TryReadGuid_MissingKeyIsReportedAsRequired(string json)
    {
        var (ok, _, error) = TryReadGuid(json, "integration_id");

        Assert.False(ok);
        Assert.Contains("is required", error);
    }

    // The error names the offending value and points at the tool that fixes
    // it — that's what lets the agent self-correct instead of retrying blind.
    [Theory]
    [InlineData("""{"integration_id":"TODO"}""", "TODO")]
    [InlineData("""{"integration_id":123}""", "123")]
    [InlineData("""{"integration_id":true}""", "True")]
    public void TryReadGuid_NonGuidIsReportedWithTheOffendingValue(string json, string expectedInMessage)
    {
        var (ok, _, error) = TryReadGuid(json, "integration_id");

        Assert.False(ok);
        Assert.Contains("must be a GUID", error);
        Assert.Contains(expectedInMessage, error);
        Assert.Contains("list_palette_actions", error);
    }

    // ─── LooksLikeTemplate ──────────────────────────────────────────────

    private static bool LooksLikeTemplate(string s)
        => (bool)Method("LooksLikeTemplate").Invoke(null, new object?[] { s })!;

    [Theory]
    [InlineData("{{ steps.x.output }}")]
    [InlineData("  {{ device.name }}  ")]
    [InlineData("prefix {{ x }} suffix")]
    public void LooksLikeTemplate_TrueForJinjaStyleReferences(string s)
        => Assert.True(LooksLikeTemplate(s));

    [Theory]
    [InlineData("plain text")]
    [InlineData("{{ unclosed")]
    [InlineData("unopened }}")]
    [InlineData("")]
    public void LooksLikeTemplate_FalseOtherwise(string s)
        => Assert.False(LooksLikeTemplate(s));
}
