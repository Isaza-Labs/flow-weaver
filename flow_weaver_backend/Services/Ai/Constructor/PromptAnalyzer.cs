using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Constructor;

// FR-002 / FR-003: ask an LLM whether the user's prompt contains enough
// information to build a workflow. If not, surface clarifying questions
// the chat agent can echo verbatim before calling create_workflow_plan.
//
// Determinism guardrails:
//   - System prompt is inlined (no configurable override) because the
//     analyzer is a structural check, not a styled response.
//   - Temperature is forced to 0 so two identical prompts produce the
//     same verdict across turns.
//   - The provider is chosen deterministically (first enabled, ordered
//     by Name) so consecutive turns don't flip between providers.
//   - Any failure — no provider, malformed JSON, network error —
//     degrades to Sufficient=true. The downstream tools
//     (EvaluatePromptSufficiencyHandler, CreateWorkflowPlanHandler)
//     catch structural gaps; this analyzer only adds a semantic layer.
public sealed class PromptAnalyzer : IPromptAnalyzer
{
    private readonly IAiProviderRepository _providers;
    private readonly ICurrentUser _caller;
    private readonly LlmProviderFactory _factory;
    private readonly ILogger<PromptAnalyzer> _logger;

    private const string SystemPrompt = """
        You are a network automation prompt analyzer. Your job is to determine whether the user's request
        contains enough information to build an automated workflow.

        A sufficient prompt must specify or imply:
        1. WHAT action to perform (backup, ping, configure, deploy, monitor, etc.)
        2. WHICH devices or device groups to target (specific IPs, roles, sites, or "all")
        3. WHEN to run (on-demand, scheduled, triggered)

        If any of these are missing or ambiguous, the prompt is insufficient.

        Respond ONLY with valid JSON in this exact format:
        {
          "sufficient": true,
          "missing_aspects": [],
          "clarifying_questions": []
        }

        If insufficient, populate missing_aspects and clarifying_questions.
        Keep questions concise and specific. Maximum 3 questions.
        Do not wrap the JSON in markdown fences. Do not add commentary before or after.
        """;

    public PromptAnalyzer(
        IAiProviderRepository providers,
        ICurrentUser caller,
        LlmProviderFactory factory,
        ILogger<PromptAnalyzer> logger)
    {
        _providers = providers;
        _caller = caller;
        _factory = factory;
        _logger = logger;
    }

    public async Task<PromptAnalysis> AnalyzeAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _logger.LogDebug("ai.prompt_analyzer.skip reason=empty_prompt");
            return new PromptAnalysis { Sufficient = true };
        }

        try
        {
            // Stable selection: order by AIProviderId rather than Name
            // so renaming a provider doesn't silently change the
            // analyzer's behavior across runs.
            var providerRow = await _providers.FindFirstEnabledOrderedByIdAsync(ct);

            if (providerRow is null)
            {
                _logger.LogDebug(
                    "ai.prompt_analyzer.skip reason=no_provider");
                return new PromptAnalysis { Sufficient = true };
            }

            var llm = await _factory.ResolveAsync(providerRow.AIProviderId, ct);
            var model = string.IsNullOrWhiteSpace(providerRow.DefaultModel)
                ? "default"
                : providerRow.DefaultModel;

            var messages = new List<LlmMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = prompt },
            };

            var result = await llm.ChatAsync(messages, model, temperature: 0.0, ct);
            return Parse(result.Content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.prompt_analyzer.failed treating_as_sufficient=true");
            return new PromptAnalysis { Sufficient = true };
        }
    }

    // Parses the LLM's JSON response. Tolerates a single fenced code block
    // (```json ... ```) because most providers occasionally emit one even
    // when told not to.
    internal PromptAnalysis Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("ai.prompt_analyzer.parse_failed reason=empty_content");
            return new PromptAnalysis { Sufficient = true };
        }

        var body = StripFences(content.Trim());
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var sufficient = root.TryGetProperty("sufficient", out var s)
                             && s.ValueKind == JsonValueKind.True;
            var missing = ReadStringList(root, "missing_aspects");
            var questions = ReadStringList(root, "clarifying_questions");

            // Self-consistency: if the model says sufficient=true but
            // returns questions, trust the questions. Operators told us
            // they'd rather see one extra clarification than a false-pass.
            if (sufficient && (missing.Count > 0 || questions.Count > 0))
            {
                _logger.LogDebug(
                    "ai.prompt_analyzer.inconsistent_response missing={Missing} questions={Questions} treated_as=insufficient",
                    missing.Count, questions.Count);
                sufficient = false;
            }

            return new PromptAnalysis
            {
                Sufficient = sufficient,
                MissingAspects = missing,
                ClarifyingQuestions = questions,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "ai.prompt_analyzer.parse_failed reason=invalid_json content_chars={Chars}",
                body.Length);
            return new PromptAnalysis { Sufficient = true };
        }
    }

    private static string StripFences(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        var inner = s[(firstNewline + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence < 0 ? inner : inner[..lastFence];
    }

    private static List<string> ReadStringList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }
}
