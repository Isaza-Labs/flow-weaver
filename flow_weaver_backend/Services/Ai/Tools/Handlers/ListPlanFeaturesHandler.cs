using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// S16 — Reads the PlanFeature rows for a WorkflowPlan or an import
// session token. The agent calls this when resuming a multi-turn task
// to recap progress without scrolling through the message history.
// Read-only; the matrix tier is autonomous.
public sealed class ListPlanFeaturesHandler : IToolHandler
{
    public string Name => "list_plan_features";

    public string Description =>
        "Tier: autonomous. List the construction features tracked under a workflow plan or " +
        "an import session, with their status (pending/in_progress/verified/rejected/skipped). " +
        "Call this when resuming a long-running task to recap what's already verified.";

    // NOTE: the "provide exactly one of plan_id / import_token" rule is enforced
    // in ExecuteAsync, NOT via a top-level `anyOf` here — OpenAI's function-calling
    // schema rejects oneOf/anyOf/allOf/enum/const/not at the top level
    // (invalid_function_parameters). See ToolSchemaOpenAiCompatTests.
    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "plan_id": { "type": "string", "format": "uuid", "description": "The workflow plan id. Provide this OR import_token." },
            "import_token": { "type": "string", "description": "The import session token. Provide this OR plan_id." }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IPlanFeatureRepository _planFeatures;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListPlanFeaturesHandler> _logger;

    public ListPlanFeaturesHandler(
        IPlanFeatureRepository planFeatures,
        ICurrentUser caller,
        ILogger<ListPlanFeaturesHandler> logger)
    {
        _planFeatures = planFeatures;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        Guid? planId = null;
        string? importToken = null;

        if (args.TryGetProperty("plan_id", out var pidEl)
            && pidEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(pidEl.GetString(), out var pid))
        {
            planId = pid;
        }

        if (args.TryGetProperty("import_token", out var itEl)
            && itEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(itEl.GetString()))
        {
            importToken = itEl.GetString();
        }

        if (planId is null && importToken is null)
        {
            _logger.LogWarning("ai.tool.list_plan_features.validation_failed reason=neither_plan_nor_token");
            return JsonSerializer.SerializeToElement(new
            {
                error = "either plan_id (uuid) or import_token is required",
            });
        }

        var rows = await _planFeatures.ListActiveAsync(planId, importToken, ct);
        var features = rows
            .Select(f => new
            {
                feature_id = f.PlanFeatureId,
                ordinal = f.Ordinal,
                title = f.Title,
                status = f.Status,
                snippet_type = f.SnippetType,
                verified_by = f.VerifiedBy,
                verified_at = f.VerifiedAt,
                rejection_reason = f.RejectionReason,
            })
            .ToList();

        var summary = features
            .GroupBy(f => f.status)
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation(
            "ai.tool.list_plan_features.ok plan_id={PlanId} import_token_set={ImportTokenSet} feature_count={Count}",
            planId, importToken is not null, features.Count);

        return JsonSerializer.SerializeToElement(new
        {
            plan_id = planId,
            import_token = importToken,
            feature_count = features.Count,
            summary,
            features,
        });
    }
}
