using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Full logs, error text, and input/output payloads for a single step run.
// Called after `get_run_details` when the agent needs to diagnose a
// specific step's failure.
//
// We cap logs at 8KB per call to stay inside the LLM's tool-result
// budget on a 200k context window. Reasoning: if the agent truly needs
// more, it can call back with a narrower step_run_id or have the user
// paste the raw logs. Cutting silently here beats blowing the context.
public sealed class GetStepLogsHandler : IToolHandler
{
    private const int MaxLogsChars = 8_000;
    private const int MaxPayloadChars = 8_000;

    public string Name => "get_step_logs";
    public string Description =>
        "Returns logs, error, input payload, and output payload for one step " +
        "run. Logs and each payload are capped (~8000 chars each) — ask the user " +
        "for the raw file if you need the full content.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "step_run_id":{"type":"string","format":"uuid"}
        },"required":["step_run_id"],"additionalProperties":false}
        """).RootElement;

    private readonly IStepRunRepository _steps;
    private readonly ICurrentUser _caller;
    private readonly ILogger<GetStepLogsHandler> _logger;

    public GetStepLogsHandler(IStepRunRepository steps, ICurrentUser caller, ILogger<GetStepLogsHandler> logger)
    {
        _steps = steps;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("step_run_id", out var idEl)
            || !Guid.TryParse(idEl.GetString(), out var stepId))
        {
            _logger.LogWarning("ai.tool.get_step_logs.validation_failed reason=step_run_id_required");
            return JsonSerializer.SerializeToElement(new { error = "step_run_id (uuid) is required" });
        }

        _logger.LogDebug("ai.tool.get_step_logs.start step_run_id={StepRunId}", stepId);

        try
        {
            // No IsActive filter — mirrors the original query, which matched on
            // (StepRunId) alone regardless of soft-delete state.
            var step = await _steps.GetByIdAsync(stepId, activeOnly: false, tracking: false, ct);
            if (step is null)
            {
                _logger.LogWarning("ai.tool.get_step_logs.not_found step_run_id={StepRunId}", stepId);
                return JsonSerializer.SerializeToElement(new { error = "step not found" });
            }

            var logs = step.Logs ?? string.Empty;
            var truncated = logs.Length > MaxLogsChars;
            if (truncated) logs = logs[..MaxLogsChars];

            _logger.LogInformation(
                "ai.tool.get_step_logs.ok step_run_id={StepRunId} status={Status} logs_truncated={Truncated}",
                stepId, step.Status, truncated);

            return JsonSerializer.SerializeToElement(new
            {
                step_run_id = step.StepRunId,
                workflow_run_id = step.WorkflowRunId,
                node_id = step.NodeId,
                snippet_id = step.SnippetId,
                device_id = step.DeviceId,
                status = step.Status,
                started_at = step.StartedAt,
                completed_at = step.CompletedAt,
                worker_id = step.WorkerId,
                error = step.Error,
                logs,
                logs_truncated = truncated,
                input_payload = CapPayload(step.InputPayload),
                output_payload = CapPayload(step.OutputPayload),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.get_step_logs.failed step_run_id={StepRunId}", stepId);
            throw;
        }
    }

    // Step payloads (an SSH info dump across many devices, a report blob)
    // can be megabytes. Returning them whole blows the LLM's context — the
    // 8KB logs cap is pointless if the payload next to it is unbounded.
    // Hand back the payload as-is when small, else a short marker so the
    // agent fetches the raw output instead of leaning on the chat context.
    private static object? CapPayload(JsonElement payload)
    {
        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        var raw = payload.GetRawText();
        if (raw.Length <= MaxPayloadChars) return payload;
        return $"[payload too large for chat: {raw.Length} chars omitted — fetch the raw step output instead of relying on the model context]";
    }
}
