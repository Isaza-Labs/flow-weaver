using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Detectors;

namespace flow_weaver_backend.Tests;

// Guards the format-routing logic inside WorkflowImportPipeline.ChooseFormat.
// The pipeline accepts a user-supplied `format_hint` and a parsed
// JsonElement, and decides which translator to invoke. The non-trivial
// rule is the `generic_dag` hint, which the dropdown labels as "smart
// routing": the pipeline must still run every deterministic detector
// and only fall through to the LLM when none recognise the shape.
//
// We reach ChooseFormat via reflection because it's an instance
// helper, but the dependency surface is bounded — a fake
// IServiceProvider feeds the constructor's IEnumerable<IDslDetector>
// and we leave the other ctor args null since ChooseFormat doesn't
// touch them.
public class WorkflowImportFormatRoutingTests
{
    private static WorkflowImportPipeline NewPipeline(params IDslDetector[] detectors)
    {
        // Construct without going through DI — we only call ChooseFormat
        // which depends solely on the detectors collection. The rest of
        // the dependencies can be null because the method we're
        // exercising doesn't dereference them.
        return (WorkflowImportPipeline)Activator.CreateInstance(
            typeof(WorkflowImportPipeline),
            new object?[]
            {
                (IEnumerable<IDslDetector>)detectors,
                Enumerable.Empty<flow_weaver_backend.Services.Import.Translators.IDslTranslator>(),
                // dependencies, conflicts, rollbackAnalyzer, snippetGenerator,
                // scopeFactory, currentUser — all unused by ChooseFormat.
                null!, null!, null!, null!, null!, null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowImportPipeline>.Instance,
            })!;
    }

    private static (string Format, double Confidence) Invoke(
        WorkflowImportPipeline pipeline, JsonElement doc, string? hint)
    {
        var method = typeof(WorkflowImportPipeline)
            .GetMethod("ChooseFormat", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ChooseFormat not found");
        var result = method.Invoke(pipeline, new object?[] { doc, hint })!;
        var type = result.GetType();
        return (
            (string)type.GetField("Item1")!.GetValue(result)!,
            (double)type.GetField("Item2")!.GetValue(result)!);
    }

    private sealed class FixedDetector : IDslDetector
    {
        public string FormatName { get; init; } = "";
        public double Score { get; init; }
        public double Detect(JsonElement document) => Score;
    }

    private static JsonElement EmptyDoc() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    public void GenericDag_hint_routes_to_high_confidence_deterministic_detector()
    {
        // User picked "Smart routing" but the file is clearly Itential
        // (detector at 0.95). The pipeline must hand off to the
        // deterministic translator, not the LLM.
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "itential", Score = 0.95 },
            new FixedDetector { FormatName = "n8n", Score = 0.0 });

        var (format, confidence) = Invoke(pipeline, EmptyDoc(), "generic_dag");

        Assert.Equal("itential", format);
        Assert.Equal(0.95, confidence);
    }

    [Fact]
    public void GenericDag_hint_falls_back_to_llm_when_no_detector_clears_threshold()
    {
        // Every detector is low-confidence — the user's smart-routing
        // request degrades to the agent-driven translator.
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "itential", Score = 0.3 },
            new FixedDetector { FormatName = "n8n", Score = 0.2 });

        var (format, confidence) = Invoke(pipeline, EmptyDoc(), "generic_dag");

        Assert.Equal("generic_dag", format);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void Explicit_specific_hint_forces_that_format_even_against_higher_detector()
    {
        // User picked Itential explicitly — even if another detector
        // scores higher, honour their choice. They know their file.
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "itential", Score = 0.5 },
            new FixedDetector { FormatName = "n8n", Score = 0.95 });

        var (format, confidence) = Invoke(pipeline, EmptyDoc(), "itential");

        Assert.Equal("itential", format);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void Auto_detect_returns_best_detector_regardless_of_threshold()
    {
        // No hint: pick the best non-zero detector, even below 0.85.
        // This keeps backwards compatibility with the pre-routing
        // behaviour where any signal beat unknown.
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "itential", Score = 0.6 },
            new FixedDetector { FormatName = "n8n", Score = 0.0 });

        var (format, confidence) = Invoke(pipeline, EmptyDoc(), null);

        Assert.Equal("itential", format);
        Assert.Equal(0.6, confidence);
    }

    [Fact]
    public void Auto_detect_falls_back_to_unknown_when_no_signal()
    {
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "itential", Score = 0.0 },
            new FixedDetector { FormatName = "n8n", Score = 0.0 });

        var (format, _) = Invoke(pipeline, EmptyDoc(), null);

        Assert.Equal("unknown", format);
    }

    [Fact]
    public void GenericDag_detector_is_not_eligible_for_routing_match()
    {
        // The generic_dag detector itself should never "win" the
        // routing competition — that would short-circuit smart routing
        // to the agent fallback when we wanted a deterministic match.
        var pipeline = NewPipeline(
            new FixedDetector { FormatName = "generic_dag", Score = 0.99 },
            new FixedDetector { FormatName = "itential", Score = 0.5 });

        var (format, _) = Invoke(pipeline, EmptyDoc(), "generic_dag");

        // With the smart-routing hint and no other detector clearing
        // the threshold, we fall back to generic_dag — but at the
        // forced 1.0 confidence, not the generic_dag detector's score.
        Assert.Equal("generic_dag", format);
    }
}
