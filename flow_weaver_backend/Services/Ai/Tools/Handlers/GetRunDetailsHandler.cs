using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Returns the header + step summary for one workflow run. Used by the
// "Fix with AI" flow: after `askAIToFix` navigates to the chat, the
// agent's first action is usually `get_run_details(run_id)` to learn
// what failed and where.
public sealed class GetRunDetailsHandler : IToolHandler
{
    public string Name => "get_run_details";
    public string Description =>
        "Returns one workflow run's status, timing, and step summary. Call " +
        "this first when the user points at a run id. Follow up with " +
        "`get_step_logs` for the failure details of a specific step.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "run_id":{"type":"string","format":"uuid"}
        },"required":["run_id"],"additionalProperties":false}
        """).RootElement;

    private readonly IWorkflowRunRepository _runs;
    private readonly IStepRunRepository _steps;
    private readonly ICurrentUser _caller;
    private readonly ILogger<GetRunDetailsHandler> _logger;

    public GetRunDetailsHandler(
        IWorkflowRunRepository runs,
        IStepRunRepository steps,
        ICurrentUser caller,
        ILogger<GetRunDetailsHandler> logger)
    {
        _runs = runs;
        _steps = steps;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // The ValueKind guard matters: GetString() throws on a non-string
        // element, and the model does occasionally emit a bare number here.
        // A tool call must always come back as a readable payload rather than
        // taking the whole chat turn down.
        if (!args.TryGetProperty("run_id", out var idEl)
            || idEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idEl.GetString(), out var runId))
        {
            _logger.LogWarning("ai.tool.get_run_details.validation_failed reason=run_id_required");
            return JsonSerializer.SerializeToElement(new { error = "run_id (uuid) is required" });
        }

        _logger.LogDebug("ai.tool.get_run_details.start run_id={RunId}", runId);

        try
        {
            // No IsActive filter on either query — mirrors the original, which
            // matched runs/steps on their ids alone.
            var run = await _runs.GetByIdAsync(runId, activeOnly: false, tracking: false, ct);
            if (run is null)
            {
                _logger.LogWarning("ai.tool.get_run_details.not_found run_id={RunId}", runId);
                return JsonSerializer.SerializeToElement(new { error = "run not found" });
            }

            var stepRows = await _steps.ListByRunUnfilteredAsync(runId, ct);
            var steps = stepRows
                .Select(s => new
                {
                    step_run_id = s.StepRunId,
                    node_id = s.NodeId,
                    status = s.Status,
                    device_id = s.DeviceId,
                    started_at = s.StartedAt,
                    completed_at = s.CompletedAt,
                    error_preview = string.IsNullOrEmpty(s.Error) ? null : s.Error.Length > 200 ? s.Error.Substring(0, 200) : s.Error,
                })
                .ToList();

            var failedCount = steps.Count(s => s.status == "failed" || s.status == "failure");
            _logger.LogInformation(
                "ai.tool.get_run_details.ok run_id={RunId} status={Status} step_count={StepCount} failed_count={FailedCount}",
                runId, run.Status, steps.Count, failedCount);

            return JsonSerializer.SerializeToElement(new
            {
                run = new
                {
                    workflow_run_id = run.WorkflowRunId,
                    workflow_id = run.WorkflowId,
                    status = run.Status,
                    trigger = run.Trigger,
                    created_by = run.CreatedBy,
                    started_at = run.StartedAt,
                    completed_at = run.CompletedAt,
                    target_device_count = run.TargetDevices.Count,
                    target_pool_count = run.TargetPools.Count,
                },
                steps,
                step_count = steps.Count,
                failed_count = failedCount,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.get_run_details.failed run_id={RunId}", runId);
            throw;
        }
    }
}
