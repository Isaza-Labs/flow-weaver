using System.Reflection;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;

namespace flow_weaver_backend.Tests;

// The upload parser and format-routing notes. Scalar typing is the subtle part:
// the v1 JSON Schema requires node.x / node.y to be NUMBERS, so a YAML `50`
// must not collapse to the string "50" — that was the bug behind using
// YamlStream instead of the typed deserializer. Reached by reflection.
public class WorkflowImportPipelineParseTests
{
    private static readonly Type Pipeline = typeof(WorkflowImportPipeline);

    private static MethodInfo Method(string name)
        => Pipeline.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    private static JsonElement ParseUpload(string text, bool withBom = false)
    {
        var bytes = withBom
            ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()
            : Encoding.UTF8.GetBytes(text);
        return (JsonElement)Method("ParseUpload").Invoke(null, new object?[] { bytes })!;
    }

    // ─── JSON branch ────────────────────────────────────────────────────

    [Fact]
    public void ParseUpload_ReadsAJsonObject()
    {
        var doc = ParseUpload("""{"workflow":{"name":"wf"}}""");

        Assert.Equal("wf", doc.GetProperty("workflow").GetProperty("name").GetString());
    }

    [Fact]
    public void ParseUpload_ReadsAJsonArray()
    {
        var doc = ParseUpload("""[{"id":"a"}]""");

        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    [Fact]
    public void ParseUpload_LeadingWhitespaceStillRoutesToJson()
    {
        var doc = ParseUpload("\n\n   {\"a\":1}");

        Assert.Equal(1, doc.GetProperty("a").GetInt32());
    }

    // A Windows tool prepends a UTF-8 BOM; without stripping it the first
    // content char isn't `{` and otherwise-valid JSON fell through to YAML.
    [Fact]
    public void ParseUpload_StripsTheUtf8BomBeforeRouting()
    {
        var doc = ParseUpload("""{"a":1}""", withBom: true);

        Assert.Equal(1, doc.GetProperty("a").GetInt32());
    }

    // ─── YAML branch: scalar typing ─────────────────────────────────────

    // The whole reason for the representation model: `x: 50` must be a NUMBER,
    // or the v1 schema rejects the node.
    [Fact]
    public void ParseUpload_BarePlainScalarsKeepTheirYamlTypes()
    {
        var doc = ParseUpload("""
            nodes:
              - id: a
                x: 50
                y: 12.5
                enabled: true
                disabled: false
                nothing: null
                tilde: ~
            """);

        var node = doc.GetProperty("nodes")[0];
        Assert.Equal(JsonValueKind.String, node.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Number, node.GetProperty("x").ValueKind);
        Assert.Equal(50, node.GetProperty("x").GetInt32());
        Assert.Equal(12.5, node.GetProperty("y").GetDouble(), 3);
        Assert.Equal(JsonValueKind.True, node.GetProperty("enabled").ValueKind);
        Assert.Equal(JsonValueKind.False, node.GetProperty("disabled").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("nothing").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("tilde").ValueKind);
    }

    // Quoting is the author saying "this is text" — a quoted `"50"` must stay
    // a string even though it looks numeric.
    [Theory]
    [InlineData("value: \"50\"")]
    [InlineData("value: '50'")]
    public void ParseUpload_QuotedScalarsStayStrings(string yaml)
    {
        var doc = ParseUpload(yaml);

        Assert.Equal(JsonValueKind.String, doc.GetProperty("value").ValueKind);
        Assert.Equal("50", doc.GetProperty("value").GetString());
    }

    // An explicit tag wins over content inference.
    [Fact]
    public void ParseUpload_ExplicitStrTagForcesAString()
    {
        var doc = ParseUpload("value: !!str 50");

        Assert.Equal(JsonValueKind.String, doc.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("TRUE", JsonValueKind.True)]
    [InlineData("True", JsonValueKind.True)]
    [InlineData("FALSE", JsonValueKind.False)]
    [InlineData("NULL", JsonValueKind.Null)]
    public void ParseUpload_BooleanAndNullRecognitionIsCaseInsensitive(string raw, JsonValueKind expected)
    {
        var doc = ParseUpload($"value: {raw}");

        Assert.Equal(expected, doc.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("-5", -5)]
    [InlineData("+7", 7)]
    [InlineData("0", 0)]
    public void ParseUpload_SignedIntegersParse(string raw, int expected)
    {
        var doc = ParseUpload($"value: {raw}");

        Assert.Equal(expected, doc.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("1e3", 1000d)]
    [InlineData("-2.5", -2.5d)]
    [InlineData(".5", 0.5d)]
    public void ParseUpload_FloatsParse(string raw, double expected)
    {
        var doc = ParseUpload($"value: {raw}");

        Assert.Equal(expected, doc.GetProperty("value").GetDouble(), 5);
    }

    // Anything that isn't a recognised scalar shape stays text — a version
    // string like 1.2.3 must not be mangled into a number.
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("show version")]
    [InlineData("10.0.0.1")]
    public void ParseUpload_NonNumericLookalikesStayStrings(string raw)
    {
        var doc = ParseUpload($"value: {raw}");

        Assert.Equal(JsonValueKind.String, doc.GetProperty("value").ValueKind);
        Assert.Equal(raw, doc.GetProperty("value").GetString());
    }

    // ─── YAML branch: structure ─────────────────────────────────────────

    [Fact]
    public void ParseUpload_NestedMappingsAndSequencesRoundTrip()
    {
        var doc = ParseUpload("""
            workflow:
              name: wf
            nodes:
              - id: a
                config_overrides:
                  commands:
                    - show version
                    - show lldp
            """);

        Assert.Equal("wf", doc.GetProperty("workflow").GetProperty("name").GetString());
        var commands = doc.GetProperty("nodes")[0].GetProperty("config_overrides").GetProperty("commands");
        Assert.Equal(2, commands.GetArrayLength());
        Assert.Equal("show lldp", commands[1].GetString());
    }

    [Fact]
    public void ParseUpload_EmptyDocumentBecomesAnEmptyObject()
    {
        var doc = ParseUpload("");

        Assert.Equal(JsonValueKind.Object, doc.ValueKind);
        Assert.Empty(doc.EnumerateObject());
    }

    // The parsed element must survive its backing document being collected —
    // the pipeline holds it well past the parse call.
    [Fact]
    public void ParseUpload_ReturnsADetachedClone()
    {
        var doc = ParseUpload("name: wf");
        GC.Collect();

        Assert.Equal("wf", doc.GetProperty("name").GetString());
    }

    // ─── BuildFormatRoutingNote ─────────────────────────────────────────

    private static string? BuildNote(string? hint, string detected, double confidence)
        => (string?)Method("BuildFormatRoutingNote").Invoke(null, new object?[] { hint, detected, confidence });

    // No hint, or a hint that matched, needs no explanation.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RoutingNote_NoHintMeansNoNote(string? hint)
        => Assert.Null(BuildNote(hint, "flowweaver_v1", 0.9));

    [Fact]
    public void RoutingNote_MatchingHintMeansNoNote()
        => Assert.Null(BuildNote("flowweaver_v1", "flowweaver_v1", 0.9));

    [Fact]
    public void RoutingNote_MatchingHintIsCaseInsensitive()
        => Assert.Null(BuildNote("FlowWeaver_V1", "flowweaver_v1", 0.9));

    // "generic_dag" means "let the system pick", so being routed elsewhere is
    // an improvement to explain, not a correction.
    [Fact]
    public void RoutingNote_GenericDagHintExplainsTheDeterministicUpgrade()
    {
        var note = BuildNote("generic_dag", "itential", 0.92);

        Assert.Contains("Routed to the deterministic 'itential' translator", note);
        Assert.Contains("92%", note);
        Assert.Contains("Deterministic translators are faster", note);
    }

    // Any other mismatch is the user being told their pick was overridden.
    [Fact]
    public void RoutingNote_ExplicitMismatchTellsTheUserWhatWasUsed()
    {
        var note = BuildNote("n8n", "itential", 0.87);

        Assert.Contains("You picked 'n8n'", note);
        Assert.Contains("detected 'itential'", note);
        Assert.Contains("87%", note);
    }

    // ─── auto-draft candidate gating ────────────────────────────────────

    private static bool IsAutoDraftCandidate(string inferredType)
        => (bool)Method("IsAutoDraftCandidate").Invoke(null, new object?[] { inferredType })!;

    // Only code-bearing types get an LLM-drafted body; everything else is
    // configuration the user must fill in.
    [Theory]
    [InlineData("python_snippet", true)]
    [InlineData("transform", true)]
    [InlineData("ssh", false)]
    [InlineData("integration_action", false)]
    [InlineData("report", false)]
    [InlineData("", false)]
    public void AutoDraftCandidate_OnlyCodeBearingTypes(string type, bool expected)
        => Assert.Equal(expected, IsAutoDraftCandidate(type));

    // ─── strong-integration gating ──────────────────────────────────────

    private static bool HasStrongIntegrationCandidate(MissingSnippet missing)
        => (bool)Method("HasStrongIntegrationCandidate").Invoke(null, new object?[] { missing })!;

    private static MissingSnippet Missing(params (string Kind, double Score)[] candidates)
        => new()
        {
            IdInImport = "s1",
            CandidatesForMapping = candidates
                .Select(c => new MappingCandidate { Kind = c.Kind, SimilarityScore = c.Score })
                .ToList(),
        };

    // A confident integration match means the task should be an
    // integration_action, so python pre-generation must be skipped.
    [Fact]
    public void StrongIntegration_ConfidentMatchGates()
        => Assert.True(HasStrongIntegrationCandidate(Missing(("integration", 0.9))));

    [Fact]
    public void StrongIntegration_ThresholdIsInclusive()
        => Assert.True(HasStrongIntegrationCandidate(Missing(("integration", 0.85))));

    [Fact]
    public void StrongIntegration_BelowThresholdDoesNotGate()
        => Assert.False(HasStrongIntegrationCandidate(Missing(("integration", 0.84))));

    // A confident match of a DIFFERENT kind is not an integration signal.
    [Fact]
    public void StrongIntegration_OtherKindsDoNotGate()
        => Assert.False(HasStrongIntegrationCandidate(Missing(("snippet", 0.99))));

    [Fact]
    public void StrongIntegration_KindMatchingIsCaseInsensitive()
        => Assert.True(HasStrongIntegrationCandidate(Missing(("Integration", 0.9))));

    [Fact]
    public void StrongIntegration_NoCandidatesDoesNotGate()
    {
        Assert.False(HasStrongIntegrationCandidate(Missing()));
        Assert.False(HasStrongIntegrationCandidate(new MissingSnippet { IdInImport = "s1" }));
    }

    [Fact]
    public void StrongIntegration_PicksTheBestOfSeveralCandidates()
        => Assert.True(HasStrongIntegrationCandidate(
            Missing(("integration", 0.4), ("snippet", 0.99), ("integration", 0.88))));
}
