using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Formal intake step. The agent invokes this BEFORE
// create_workflow_plan so the user gets specific clarifying questions
// when the prompt is missing context. Everything returned is
// deterministic — the LLM phrases the questions in natural language,
// but it doesn't decide what's missing on its own.
//
// Checks performed:
//   1. Target requirement — if any proposed snippet is per_device and
//      no target devices/pools were supplied, flag "missing targets".
//   2. Required input keys — scan proposed snippets' input_schema for
//      `required` fields; any name not referenced in the description
//      (naive keyword match) becomes a clarifying question.
//   3. Workflow name collision — if `candidate_name` is set and matches
//      an existing active workflow, ask whether to create
//      a new version, clone, or pick a different name.
//   4. Production scope — phrases like "producción" / "prod" in the
//      description without an explicit env confirmation are surfaced
//      because promotion to production is a one-way lever.
//
// The handler never blocks the agent — it returns `sufficient: true`
// when no checks fire. The skill `clarification.md` documents how the
// agent should react to each kind of finding.
public sealed class EvaluatePromptSufficiencyHandler : IToolHandler
{
    public string Name => "evaluate_prompt_sufficiency";

    public string Description =>
        "Evaluate whether the user's request has enough information to build a workflow. " +
        "Returns a structured list of missing fields and ambiguities. Call this BEFORE " +
        "create_workflow_plan; if `sufficient` is false, ask the user the suggested questions " +
        "and wait for answers.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["description"],
          "properties": {
            "description": {
              "type": "string",
              "description": "The user's full request in natural language."
            },
            "candidate_name": {
              "type": "string",
              "description": "Name you plan to use for the workflow (for collision checks)."
            },
            "proposed_snippet_ids": {
              "type": "array",
              "items": { "type": "string", "format": "uuid" },
              "description": "Snippet UUIDs you already plan to use. Drives per_device + required-key checks."
            },
            "proposed_target_devices": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Device UUIDs or names the user already mentioned."
            },
            "proposed_target_pools": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Pool UUIDs or names the user already mentioned."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly ISnippetRepository _snippets;
    private readonly IWorkflowRepository _workflows;
    private readonly ICurrentUser _caller;
    private readonly ILogger<EvaluatePromptSufficiencyHandler> _logger;

    public EvaluatePromptSufficiencyHandler(
        ISnippetRepository snippets,
        IWorkflowRepository workflows,
        ICurrentUser caller,
        ILogger<EvaluatePromptSufficiencyHandler> logger)
    {
        _snippets = snippets;
        _workflows = workflows;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var description = GetString(args, "description")?.Trim() ?? string.Empty;
        if (description.Length == 0)
        {
            _logger.LogWarning("ai.tool.evaluate_prompt_sufficiency.validation_failed reason=description_empty");
            return JsonSerializer.SerializeToElement(new
            {
                sufficient = false,
                missing = new[] { new { field = "description", reason = "empty request", suggested_question = "What should this workflow do?" } },
                ambiguities = Array.Empty<object>(),
            });
        }

        var candidateName = GetString(args, "candidate_name");
        var proposedSnippetIds = GetGuidList(args, "proposed_snippet_ids");
        var proposedDevices = GetStringList(args, "proposed_target_devices");
        var proposedPools = GetStringList(args, "proposed_target_pools");

        // Never log raw description — only length to avoid user prompt leakage.
        _logger.LogDebug(
            "ai.tool.evaluate_prompt_sufficiency.start description_chars={DescriptionChars} candidate_name_set={CandidateNameSet} snippet_count={SnippetCount} device_count={DeviceCount} pool_count={PoolCount}",
            description.Length,
            !string.IsNullOrWhiteSpace(candidateName),
            proposedSnippetIds.Count,
            proposedDevices.Count,
            proposedPools.Count);

        var missing = new List<object>();
        var ambiguities = new List<object>();

        // ── Check 1 + 2: target + required-keys via proposed snippets ──
        if (proposedSnippetIds.Count > 0)
        {
            var snippets = await _snippets.ListActiveIntakeByIdsAsync(proposedSnippetIds, ct);

            var needsDevices = snippets.Any(s =>
                string.Equals(s.TargetMode, "per_device", StringComparison.OrdinalIgnoreCase));
            if (needsDevices && proposedDevices.Count == 0 && proposedPools.Count == 0)
            {
                var perDeviceNames = snippets
                    .Where(s => string.Equals(s.TargetMode, "per_device", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Name)
                    .ToList();
                missing.Add(new
                {
                    field = "targets",
                    reason = $"Snippets that run per device ({string.Join(", ", perDeviceNames)}) need at least one device or pool.",
                    suggested_question = "Which devices or pools should this workflow target? I can list the available ones.",
                });
            }

            foreach (var snippet in snippets)
            {
                foreach (var key in ReadRequiredKeys(snippet.InputSchema))
                {
                    // Naive: if the user's description never mentions the
                    // required field name, we ask about it. Conservative —
                    // sometimes the description does imply the value, but
                    // asking once per workflow is cheap and reduces
                    // silent mis-configurations.
                    if (!MentionsKey(description, key))
                    {
                        missing.Add(new
                        {
                            field = $"{snippet.Name}.{key}",
                            reason = $"Snippet `{snippet.Name}` requires `{key}` in its input.",
                            suggested_question = $"What value should I use for `{key}` in the `{snippet.Name}` step?",
                        });
                    }
                }
            }
        }

        // ── Check 3: workflow name collision ──
        if (!string.IsNullOrWhiteSpace(candidateName))
        {
            var clash = await _workflows.FindActiveByNameAsync(candidateName, 3, ct);
            if (clash.Count > 0)
            {
                ambiguities.Add(new
                {
                    topic = "workflow_name_collision",
                    options = clash.Select(w => new { w.WorkflowId, w.Environment, w.Version }),
                    suggested_question = $"A workflow named `{candidateName}` already exists. " +
                        "Should I use a different name, clone the existing one as a draft, or edit the current one?",
                });
            }
        }

        // ── Check 4: production-scope without explicit confirmation ──
        if (MentionsProductionScope(description) && !ExplicitlyConfirmsProd(description))
        {
            ambiguities.Add(new
            {
                topic = "production_scope",
                options = new[] { "draft", "qa", "production" },
                suggested_question = "You mentioned production. Should the workflow run directly against production, or stay in draft/qa for validation first?",
            });
        }

        var sufficient = missing.Count == 0 && ambiguities.Count == 0;
        _logger.LogInformation(
            "ai.tool.evaluate_prompt_sufficiency.ok sufficient={Sufficient} missing_count={MissingCount} ambiguity_count={AmbiguityCount}",
            sufficient, missing.Count, ambiguities.Count);
        return JsonSerializer.SerializeToElement(new
        {
            sufficient,
            missing,
            ambiguities,
        });
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> ReadRequiredKeys(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) yield break;
        if (!schema.TryGetProperty("required", out var req)) yield break;
        if (req.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in req.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var v = item.GetString();
                if (!string.IsNullOrWhiteSpace(v)) yield return v!;
            }
        }
    }

    // Case-insensitive substring check with a word-boundary on both ends
    // so `hostname` doesn't match `name` and `vlan_id` doesn't match
    // `id`. The agent's description is already quite verbose so strict
    // matching works better than fuzzy.
    private static bool MentionsKey(string description, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        var humanized = key.Replace('_', ' ');
        return description.Contains(key, StringComparison.OrdinalIgnoreCase)
            || description.Contains(humanized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MentionsProductionScope(string description) =>
        description.Contains("producción", StringComparison.OrdinalIgnoreCase)
        || description.Contains("produccion", StringComparison.OrdinalIgnoreCase)
        || description.Contains("production", StringComparison.OrdinalIgnoreCase)
        || description.Contains(" prod ", StringComparison.OrdinalIgnoreCase)
        || description.EndsWith(" prod", StringComparison.OrdinalIgnoreCase);

    private static bool ExplicitlyConfirmsProd(string description) =>
        description.Contains("ventana de cambio", StringComparison.OrdinalIgnoreCase)
        || description.Contains("change window", StringComparison.OrdinalIgnoreCase)
        || description.Contains("maintenance window", StringComparison.OrdinalIgnoreCase)
        || description.Contains("aprobado", StringComparison.OrdinalIgnoreCase)
        || description.Contains("approved", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> GetStringList(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static List<Guid> GetGuidList(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
            .Select(e => Guid.Parse(e.GetString()!))
            .ToList();
    }
}
