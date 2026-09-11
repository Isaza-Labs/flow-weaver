using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Import.Translators;

namespace flow_weaver_backend.Tests;

// The Itential-specific classification helpers. Itential's WorkFlowEngine
// tasks are the ones with no FlowWeaver equivalent — they run arbitrary
// logic — so how they are classified decides whether the wizard asks the AI
// to draft a body or tries (and fails) to map them onto a built-in type.
// Reached by reflection; these are private statics.
public class ItentialTranslatorInternalTests
{
    private static readonly Type Translator = typeof(ItentialTranslator);

    private static MethodInfo Method(string name)
        => Translator.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    // ─── ClassifyWfe ────────────────────────────────────────────────────

    private static string ClassifyWfe(string task)
        => Method("ClassifyWfe").Invoke(null, new object?[] { TestJson.Element(task) })!.ToString()!;

    // `app == "WorkFlowEngine"` is the authoritative signal in every real
    // export.
    [Theory]
    [InlineData("evaluation", "Evaluation")]
    [InlineData("transformation", "Transformation")]
    [InlineData("viewData", "ViewData")]
    [InlineData("updateJobDescription", "UpdateJobDescription")]
    [InlineData("stub", "Stub")]
    [InlineData("somethingElse", "Other")]
    public void Classify_RecognisesEachWfeTaskName(string name, string expected)
    {
        var task = $$"""{"app":"WorkFlowEngine","name":"{{name}}"}""";

        Assert.Equal(expected, ClassifyWfe(task));
    }

    [Fact]
    public void Classify_TaskNameMatchingIsCaseInsensitive()
    {
        Assert.Equal("Evaluation", ClassifyWfe("""{"app":"WorkFlowEngine","name":"EVALUATION"}"""));
    }

    // A task from any other app is not a WFE task at all.
    [Theory]
    [InlineData("""{"app":"HttpAdapter","name":"evaluation"}""")]
    [InlineData("""{"name":"evaluation"}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public void Classify_NonWfeTasksAreNone(string task)
    {
        Assert.Equal("None", ClassifyWfe(task));
    }

    // The safety net for exports where `app` was dropped in migration.
    [Fact]
    public void Classify_DisplayNameIsAFallbackSignal()
    {
        Assert.Equal("Transformation",
            ClassifyWfe("""{"displayName":"WorkFlowEngine","name":"transformation"}"""));
    }

    // `displayName == "Tools"` alone is too loose — custom adapters use it
    // too — so it must NOT be treated as a WFE signal.
    [Fact]
    public void Classify_DisplayNameToolsAloneIsNotAWfeSignal()
    {
        Assert.Equal("None", ClassifyWfe("""{"displayName":"Tools","name":"transformation"}"""));
    }

    // `canvasName` is the fallback when `name` is absent.
    [Fact]
    public void Classify_FallsBackToCanvasName()
    {
        Assert.Equal("Stub",
            ClassifyWfe("""{"app":"WorkFlowEngine","canvasName":"stub"}"""));
    }

    // ─── MapWfeKindToSnippetId ──────────────────────────────────────────

    private static string? MapWfeKind(string kindName)
    {
        var kindType = Translator.GetNestedType("WfeKind", BindingFlags.NonPublic)
            ?? Translator.Assembly.GetTypes().Single(t => t.Name == "WfeKind");
        var kind = Enum.Parse(kindType, kindName);
        return (string?)Method("MapWfeKindToSnippetId")
            .Invoke(null, new object?[] { kind, TestJson.Element("{}"), "", "" });
    }

    // Anything that genuinely runs custom logic becomes a python_snippet so
    // the wizard's "Generate with AI" step takes over.
    [Theory]
    [InlineData("Transformation")]
    [InlineData("ViewData")]
    [InlineData("UpdateJobDescription")]
    [InlineData("Stub")]
    [InlineData("Other")]
    public void MapKind_CustomLogicBecomesAPythonSnippet(string kind)
    {
        Assert.Equal("python_snippet", MapWfeKind(kind));
    }

    // `None` means "not a WFE task", so the caller falls back to its
    // app-based heuristic rather than being handed a type.
    [Fact]
    public void MapKind_NoneDefersToTheCaller()
    {
        Assert.Null(MapWfeKind("None"));
    }

    // ─── ResolveSnippetIdForTask ────────────────────────────────────────

    private static string ResolveSnippetId(string app, string command = "")
        => (string)Method("ResolveSnippetIdForTask").Invoke(null, new object?[] { app, command })!;

    // The app sets hold EXACT adapter names, not prefixes.
    [Theory]
    [InlineData("@itential/adapter-http")]
    [InlineData("http")]
    [InlineData("HTTP")]
    [InlineData("rest")]
    [InlineData("request")]
    public void ResolveSnippetId_HttpAppsBecomeRestCall(string app)
    {
        Assert.Equal("rest_call", ResolveSnippetId(app));
    }

    [Theory]
    [InlineData("@itential/adapter-ssh")]
    [InlineData("ssh")]
    [InlineData("netmiko")]
    [InlineData("cli")]
    public void ResolveSnippetId_SshAppsBecomeSsh(string app)
    {
        Assert.Equal("ssh", ResolveSnippetId(app));
    }

    [Fact]
    public void ResolveSnippetId_PythonAppsBecomePythonSnippet()
    {
        Assert.Equal("python_snippet", ResolveSnippetId("MyPythonAdapter"));
        Assert.Equal("python_snippet", ResolveSnippetId("PYTHON"));
    }

    // An unknown app with a name is assumed to be an external system, so the
    // user maps it to an integration in the wizard.
    [Fact]
    public void ResolveSnippetId_UnknownAppsBecomeIntegrationActions()
    {
        Assert.Equal("integration_action", ResolveSnippetId("AcmeCustomAdapter"));
    }

    // With no app at all there is nothing to map to, so it falls back to
    // custom code.
    [Fact]
    public void ResolveSnippetId_AnEmptyAppFallsBackToPython()
    {
        Assert.Equal("python_snippet", ResolveSnippetId(""));
    }

    // ─── MapStateToEdge ─────────────────────────────────────────────────

    private static string MapStateToEdge(string state)
        => (string)Method("MapStateToEdge").Invoke(null, new object?[] { state })!;

    // Itential's transition states map onto FlowWeaver's three edge types;
    // an unknown state becomes `always` so the edge is never dropped.
    [Theory]
    [InlineData("success", "success")]
    [InlineData("SUCCESS", "success")]
    [InlineData("failure", "failure")]
    [InlineData("error", "failure")]
    [InlineData("ERROR", "failure")]
    [InlineData("finished", "always")]
    [InlineData("", "always")]
    public void MapState_CoversEveryTransitionState(string state, string expected)
    {
        Assert.Equal(expected, MapStateToEdge(state));
    }

    // ─── NormaliseOperator / InvertOperator ─────────────────────────────

    private static string? NormaliseOperator(string raw)
        => (string?)Method("NormaliseOperator").Invoke(null, new object?[] { raw });

    // Itential writes single `=` for equality; FlowWeaver's evaluator wants
    // `==`, and an unrecognised operator must be rejected rather than passed
    // through to produce a silently-broken condition.
    [Theory]
    [InlineData("=", "==")]
    [InlineData("==", "==")]
    [InlineData(">", ">")]
    [InlineData("<", "<")]
    [InlineData(">=", ">=")]
    [InlineData("<=", "<=")]
    [InlineData("!=", "!=")]
    public void Normalise_MapsEveryKnownOperator(string raw, string expected)
    {
        Assert.Equal(expected, NormaliseOperator(raw));
    }

    [Theory]
    [InlineData("===")]
    [InlineData("contains")]
    [InlineData("")]
    [InlineData("=~")]
    public void Normalise_RejectsUnknownOperators(string raw)
    {
        Assert.Null(NormaliseOperator(raw));
    }

    private static string? InvertOperator(string? op)
        => (string?)Method("InvertOperator").Invoke(null, new object?[] { op });

    // The inverse drives the "else" edge of a collapsed evaluation; getting
    // it wrong sends the failure branch down the success path.
    [Theory]
    [InlineData(">", "<=")]
    [InlineData("<", ">=")]
    [InlineData(">=", "<")]
    [InlineData("<=", ">")]
    [InlineData("==", "!=")]
    [InlineData("!=", "==")]
    public void Invert_ProducesTheLogicalComplement(string op, string expected)
    {
        Assert.Equal(expected, InvertOperator(op));
    }

    // Inverting twice must return the original — otherwise the two branches
    // of a decision would not be exhaustive.
    [Theory]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData("==")]
    [InlineData("!=")]
    public void Invert_IsItsOwnInverse(string op)
    {
        Assert.Equal(op, InvertOperator(InvertOperator(op)));
    }

    [Theory]
    [InlineData("=")]      // must be normalised first
    [InlineData("unknown")]
    [InlineData(null)]
    public void Invert_RejectsWhatItCannotComplement(string? op)
    {
        Assert.Null(InvertOperator(op));
    }

    // ─── SanitiseSnippetPlaceholder ─────────────────────────────────────

    private static string SanitisePlaceholder(string raw, string kindName)
    {
        var kindType = Translator.Assembly.GetTypes().Single(t => t.Name == "WfeKind");
        var kind = Enum.Parse(kindType, kindName);
        return (string)Method("SanitiseSnippetPlaceholder").Invoke(null, new object?[] { raw, kind })!;
    }

    // The placeholder id becomes both the wizard's "missing snippet" label and
    // the AI generator's hint, so it must be a usable identifier.
    [Fact]
    public void Sanitise_ProducesALowercaseIdentifier()
    {
        var result = SanitisePlaceholder("Transform Device List", "Transformation");

        Assert.Equal(result, result.ToLowerInvariant());
        Assert.DoesNotContain(" ", result);
    }

    // With nothing usable in the label the id is still non-empty and keeps the
    // `python_` prefix, which is what steers DependencyResolver.InferSnippetType
    // to python_snippet — a blank id would land the placeholder in the wrong
    // bucket entirely.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Sanitise_StillYieldsASteerablePythonId(string raw)
    {
        var result = SanitisePlaceholder(raw, "Transformation");

        Assert.StartsWith("python_", result);
        Assert.True(result.Length > "python_".Length, result);
    }

    // Two placeholders from the same kind must not collide, or the wizard
    // would show one "missing snippet" row for several distinct tasks.
    [Fact]
    public void Sanitise_UnnamedPlaceholdersAreUnique()
    {
        var first = SanitisePlaceholder("", "Transformation");
        var second = SanitisePlaceholder("", "Transformation");

        Assert.NotEqual(first, second);
    }

    // ─── BuildEdge ──────────────────────────────────────────────────────

    private static JsonElement BuildEdge(string source, string target, string type)
        => TestJson.Element(JsonSerializer.Serialize(
            Method("BuildEdge").Invoke(null, new object?[] { source, target, type })));

    [Fact]
    public void BuildEdge_EmitsTheV1EdgeShape()
    {
        var edge = BuildEdge("a", "b", "success");

        Assert.Equal("a", edge.GetProperty("source").GetString());
        Assert.Equal("b", edge.GetProperty("target").GetString());
        Assert.Equal("success", edge.GetProperty("type").GetString());
    }

    // ─── FormatName ─────────────────────────────────────────────────────

    [Fact]
    public void TheTranslatorRegistersUnderItential()
    {
        var translator = (ItentialTranslator)Activator.CreateInstance(typeof(ItentialTranslator))!;

        Assert.Equal("itential", translator.FormatName);
    }
}
