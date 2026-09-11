using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Identity;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Governance-plan creator. Plans exist for the qa/production review
// path — they go through submit_plan_for_approval → approve_plan →
// build_plan before any workflow is materialized.
//
// Explicitly NOT for draft creation. For "create a workflow that does
// X" requests the caller should use fw_workflows:create_workflow with
// environment=draft. That's a single-tool call the user approves once;
// the plan flow adds four extra confirmations (draft → submit → approve
// → build → run) that the operator never asked for.
//
// This handler rejects draft-targeted requests up-front so the agent
// can't accidentally route a draft through the governance flow. The
// skill guidance in governance.md + base.md tells the LLM when the
// plan flow is appropriate.
public sealed class CreateWorkflowPlanHandler : IToolHandler
{
    private static readonly HashSet<string> AllowedEnvironments = new(StringComparer.OrdinalIgnoreCase)
    {
        "qa", "production",
    };

    public string Name => "create_workflow_plan";

    public string Description =>
        "Tier: elevated_confirm. Governance-plan creator for qa/production-bound changes ONLY. " +
        "Rejects target_environment=draft with an explicit error — for draft creation call " +
        "fw_workflows:create_workflow directly in a single Plan block. Use this when the user " +
        "asked for review/approval, when the change lands in production, or when policy " +
        "requires a plan. The plan must be submitted + approved + built (admin-only) before it " +
        "becomes a real workflow.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","required":["intent","steps","target_environment"],"properties":{
          "intent":{"type":"string","description":"What the workflow should accomplish"},
          "description":{"type":"string"},
          "target_environment":{"type":"string","enum":["qa","production"],"description":"Environment the built workflow will land in. Draft is NOT allowed — use fw_workflows:create_workflow for drafts."},
          "steps":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"service_type":{"type":"string"},"config":{"type":"object"}}}},
          "services_to_create":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"type":{"type":"string"},"description":{"type":"string"}}}},
          "services_to_reuse":{"type":"array","items":{"type":"string"}},
          "target_devices":{"type":"array","items":{"type":"string"}},
          "target_pools":{"type":"array","items":{"type":"string"}},
          "risks":{"type":"array","items":{"type":"string"}}
        },"additionalProperties":false}
        """).RootElement;

    private readonly IRepository<WorkflowPlanModel> _plans;
    private readonly IPlanFeatureRepository _planFeatures;
    private readonly ICurrentUser _caller;
    private readonly ILogger<CreateWorkflowPlanHandler> _logger;

    public CreateWorkflowPlanHandler(
        IRepository<WorkflowPlanModel> plans,
        IPlanFeatureRepository planFeatures,
        ICurrentUser caller,
        ILogger<CreateWorkflowPlanHandler> logger)
    {
        _plans = plans;
        _planFeatures = planFeatures;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var intent = args.TryGetProperty("intent", out var i) ? i.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(intent))
        {
            _logger.LogWarning("ai.tool.create_workflow_plan.validation_failed reason=intent_required");
            return JsonSerializer.SerializeToElement(new { error = "intent is required" });
        }

        // Hard gate: draft requests never go through the plan flow.
        // The whole point of this change is to stop the agent from
        // routing "create a workflow that pings X" through five
        // governance steps when the user wanted one.
        if (!args.TryGetProperty("target_environment", out var envEl)
            || envEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(envEl.GetString()))
        {
            _logger.LogWarning("ai.tool.create_workflow_plan.validation_failed reason=target_environment_required");
            return JsonSerializer.SerializeToElement(new
            {
                error = "target_environment is required (allowed: qa, production). " +
                        "For draft workflows, call fw_workflows:create_workflow directly instead.",
            });
        }

        var env = envEl.GetString()!;
        if (!AllowedEnvironments.Contains(env))
        {
            _logger.LogWarning(
                "ai.tool.create_workflow_plan.validation_failed reason=invalid_environment target_environment={TargetEnvironment}",
                env);
            return JsonSerializer.SerializeToElement(new
            {
                error = $"target_environment '{env}' not allowed. " +
                        "Draft workflows must use fw_workflows:create_workflow directly, " +
                        "not the governance plan flow. Plans exist for qa/production review.",
            });
        }

        _logger.LogDebug(
            "ai.tool.create_workflow_plan.start target_environment={TargetEnvironment} intent_chars={IntentChars}",
            env, intent.Length);

        try
        {
            var now = DateTime.UtcNow;
            var plan = new WorkflowPlanModel
            {
                WorkflowPlanId = Guid.NewGuid(),
                Intent = intent,
                Description = args.TryGetProperty("description", out var d) ? d.GetString() : null,
                Steps = args.TryGetProperty("steps", out var s) ? s : default,
                ServicesToCreate = args.TryGetProperty("services_to_create", out var sc) ? sc : default,
                ServicesToReuse = args.TryGetProperty("services_to_reuse", out var sr) ? sr : default,
                Risks = args.TryGetProperty("risks", out var r) ? r : default,
                TargetDevices = ParseGuidList(args, "target_devices"),
                TargetPools = ParseGuidList(args, "target_pools"),
                Status = "draft",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _plans.Add(plan);

            // S16 — Harness lesson 8: materialize one PlanFeature per
            // step so list_plan_features can rebuild the checklist on
            // resume. Pre-saved as `pending`; tools that verify a step
            // (simulate_workflow_run, run_acceptance_tests) flip the
            // status to `verified` later.
            var featureCount = 0;
            if (args.TryGetProperty("steps", out var stepsEl)
                && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var ordinal = 0;
                foreach (var step in stepsEl.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object) continue;
                    var title = step.TryGetProperty("name", out var nEl)
                        && nEl.ValueKind == JsonValueKind.String
                            ? (nEl.GetString() ?? string.Empty).Trim()
                            : string.Empty;
                    if (title.Length == 0) title = $"step_{ordinal + 1}";
                    var snippetType = step.TryGetProperty("service_type", out var stEl)
                        && stEl.ValueKind == JsonValueKind.String
                            ? stEl.GetString()
                            : null;
                    _planFeatures.Add(new PlanFeature
                    {
                        PlanFeatureId = Guid.NewGuid(),
                        WorkflowPlanId = plan.WorkflowPlanId,
                        Ordinal = ordinal++,
                        Title = title,
                        SnippetType = snippetType,
                        Status = PlanFeatureStatus.Pending,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    featureCount++;
                }
            }

            // Both repos share the scoped AppDbContext, so persisting the
            // aggregate root flushes the staged PlanFeature rows too.
            await _plans.SaveChangesAsync(ct);

            _logger.LogInformation(
                "ai.tool.create_workflow_plan.ok plan_id={PlanId} target_environment={TargetEnvironment} feature_count={FeatureCount}",
                plan.WorkflowPlanId, env, featureCount);

            return JsonSerializer.SerializeToElement(new
            {
                plan_id = plan.WorkflowPlanId,
                status = plan.Status,
                target_environment = env,
                next_step = "submit_plan_for_approval",
                message = $"Governance plan created for {env}. Next: submit_plan_for_approval, then admin approve_plan, then admin build_plan.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.create_workflow_plan.failed target_environment={TargetEnvironment}", env);
            throw;
        }
    }

    private static List<Guid> ParseGuidList(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
            .Select(e => Guid.Parse(e.GetString()!))
            .ToList();
    }
}
