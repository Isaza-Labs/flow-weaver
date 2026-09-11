using System.Text.Json;
using flow_weaver_backend.Services.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The remaining two template namespaces: `{{ input.X }}` (the run's own input
// payload) and `{{ run.X }}` (run provenance — id, workflow name, trigger).
//
// These are what let one workflow definition behave differently per run. A
// silent miss here does not crash: the literal template text survives into the
// step payload, which is why the executor's unresolved-template scan exists —
// both halves are pinned together.
public class VariableResolverContextTests
{
    private static VariableResolver Resolver()
        => new(NullLogger<VariableResolver>.Instance);

    private static JsonElement Resolve(
        string payload,
        string? runInput = null,
        string? runContext = null,
        string? device = null,
        Dictionary<string, StepResult>? steps = null)
        => Resolver().Resolve(
            TestJson.Element(payload),
            steps ?? new Dictionary<string, StepResult>(),
            device is null ? null : TestJson.Element(device),
            runInput is null ? null : TestJson.Element(runInput),
            runContext is null ? null : TestJson.Element(runContext));

    // ─── input.* ────────────────────────────────────────────────────────

    [Fact]
    public void Input_AWholeStringTemplateResolvesFromTheRunInput()
    {
        var result = Resolve(
            """{"site":"{{ input.site }}"}""",
            runInput: """{"site":"madrid"}""");

        Assert.Equal("madrid", result.GetProperty("site").GetString());
    }

    // Type preservation matters here as much as for steps: a numeric input
    // must stay a number so a downstream comparison works.
    [Theory]
    [InlineData("""{"n":42}""", JsonValueKind.Number)]
    [InlineData("""{"n":true}""", JsonValueKind.True)]
    [InlineData("""{"n":["a"]}""", JsonValueKind.Array)]
    [InlineData("""{"n":{"k":1}}""", JsonValueKind.Object)]
    public void Input_TheValueTypeIsPreserved(string runInput, JsonValueKind expected)
    {
        var result = Resolve("""{"v":"{{ input.n }}"}""", runInput: runInput);

        Assert.Equal(expected, result.GetProperty("v").ValueKind);
    }

    [Fact]
    public void Input_ANestedPathResolves()
    {
        var result = Resolve(
            """{"host":"{{ input.target.host }}"}""",
            runInput: """{"target":{"host":"10.0.0.1"}}""");

        Assert.Equal("10.0.0.1", result.GetProperty("host").GetString());
    }

    [Fact]
    public void Input_AnInlineTemplateIsStringified()
    {
        var result = Resolve(
            """{"cmd":"deploy to {{ input.site }} now"}""",
            runInput: """{"site":"madrid"}""");

        Assert.Equal("deploy to madrid now", result.GetProperty("cmd").GetString());
    }

    // A missing key leaves the literal text so the executor's unresolved
    // scan can fail the step rather than shipping an empty value.
    [Fact]
    public void Input_AMissingKeyLeavesTheLiteralTemplate()
    {
        var result = Resolve("""{"site":"{{ input.site }}"}""", runInput: """{"other":1}""");

        Assert.Equal("{{ input.site }}", result.GetProperty("site").GetString());
        Assert.NotEmpty(VariableResolver.FindUnresolvedTemplates(result));
    }

    [Fact]
    public void Input_WithNoRunInputAtAllTheTemplateStaysLiteral()
    {
        var result = Resolve("""{"site":"{{ input.site }}"}""");

        Assert.Equal("{{ input.site }}", result.GetProperty("site").GetString());
    }

    // ─── run.* ──────────────────────────────────────────────────────────

    private const string RunContext = """
        {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","workflow_id":"11111111-1111-1111-1111-111111111111",
         "workflow_name":"lldp-sync","environment":"production","trigger":"schedule",
         "started_at":"2026-07-28T10:00:00.0000000Z"}
        """;

    [Theory]
    [InlineData("workflow_name", "lldp-sync")]
    [InlineData("environment", "production")]
    [InlineData("trigger", "schedule")]
    public void Run_ProvenanceFieldsResolve(string field, string expected)
    {
        var result = Resolve(
            "{\"v\":\"{{ run." + field + " }}\"}", runContext: RunContext);

        Assert.Equal(expected, result.GetProperty("v").GetString());
    }

    // The run id is what makes a report or a ticket traceable back to the
    // run that produced it.
    [Fact]
    public void Run_TheRunIdIsAvailableForTraceability()
    {
        var result = Resolve(
            """{"title":"report for run {{ run.id }}"}""", runContext: RunContext);

        Assert.Contains("3f2504e0", result.GetProperty("title").GetString());
    }

    [Fact]
    public void Run_AMissingFieldLeavesTheLiteralTemplate()
    {
        var result = Resolve("""{"v":"{{ run.nope }}"}""", runContext: RunContext);

        Assert.Equal("{{ run.nope }}", result.GetProperty("v").GetString());
    }

    [Fact]
    public void Run_WithNoRunContextTheTemplateStaysLiteral()
    {
        var result = Resolve("""{"v":"{{ run.trigger }}"}""");

        Assert.Equal("{{ run.trigger }}", result.GetProperty("v").GetString());
    }

    // ─── Namespaces together ────────────────────────────────────────────

    // A single payload routinely mixes all four namespaces — each has to
    // resolve from its own source without shadowing the others.
    [Fact]
    public void AllFourNamespacesResolveSideBySide()
    {
        var steps = new Dictionary<string, StepResult>
        {
            ["collect"] = new(TestJson.Element("""{"count":3}""")),
        };

        var result = Resolve(
            """
            {"a":"{{ steps.collect.output.count }}","b":"{{ device.name }}",
             "c":"{{ input.site }}","d":"{{ run.trigger }}"}
            """,
            runInput: """{"site":"madrid"}""",
            runContext: RunContext,
            device: """{"name":"r1"}""",
            steps: steps);

        Assert.Equal(3, result.GetProperty("a").GetInt32());
        Assert.Equal("r1", result.GetProperty("b").GetString());
        Assert.Equal("madrid", result.GetProperty("c").GetString());
        Assert.Equal("schedule", result.GetProperty("d").GetString());
        Assert.Empty(VariableResolver.FindUnresolvedTemplates(result));
    }

    // Two templates from different namespaces in one string both substitute.
    [Fact]
    public void MixedInlineTemplatesAllSubstitute()
    {
        var result = Resolve(
            """{"msg":"{{ run.workflow_name }} on {{ device.name }} for {{ input.site }}"}""",
            runInput: """{"site":"madrid"}""",
            runContext: RunContext,
            device: """{"name":"r1"}""");

        Assert.Equal("lldp-sync on r1 for madrid", result.GetProperty("msg").GetString());
    }

    // A partial miss in an inline string leaves only the unresolved part
    // literal, so the scan still flags the payload.
    [Fact]
    public void APartialInlineMissIsStillDetectable()
    {
        var result = Resolve(
            """{"msg":"{{ run.trigger }} / {{ input.missing }}"}""",
            runInput: "{}",
            runContext: RunContext);

        var msg = result.GetProperty("msg").GetString()!;
        Assert.StartsWith("schedule /", msg);
        Assert.Contains("{{ input.missing }}", msg);
        Assert.NotEmpty(VariableResolver.FindUnresolvedTemplates(result));
    }

    // ─── FindUnresolvedTemplates ────────────────────────────────────────

    [Fact]
    public void Scan_ReportsTheLocationOfEachResidualTemplate()
    {
        var unresolved = VariableResolver.FindUnresolvedTemplates(TestJson.Element(
            """{"outer":{"cmd":"{{ steps.ghost.output.x }}"}}"""));

        var entry = Assert.Single(unresolved);
        Assert.Contains("cmd", entry.Location);
        Assert.Contains("steps.ghost", entry.TemplateText);
    }

    [Fact]
    public void Scan_WalksArrays()
    {
        var unresolved = VariableResolver.FindUnresolvedTemplates(TestJson.Element(
            """{"cmds":["ok","{{ input.missing }}"]}"""));

        Assert.Single(unresolved);
    }

    [Fact]
    public void Scan_AFullyResolvedPayloadReportsNothing()
    {
        Assert.Empty(VariableResolver.FindUnresolvedTemplates(
            TestJson.Element("""{"cmd":"show version","n":1,"b":true,"z":null}""")));
    }

    // Text that merely contains braces is not a template and must not be
    // reported — a JSON body in a rest_call config would otherwise always
    // look broken.
    [Theory]
    [InlineData("""{"body":"{\"k\":1}"}""")]
    [InlineData("""{"s":"use {braces} carefully"}""")]
    public void Scan_PlainBracesAreNotTemplates(string payload)
    {
        Assert.Empty(VariableResolver.FindUnresolvedTemplates(TestJson.Element(payload)));
    }
}
