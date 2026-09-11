using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// S16 — Harness lesson 9 ("no victory until verified"). The agent
// invokes this tool when it believes a draft workflow is complete and
// ready to promote. The handler refuses unless:
//
//   1. Workflow.LastSimulationId points to a SimulationResult row.
//   2. That row's Ok flag is true (no structural issues).
//   3. That row's SchemaHash still matches the current (Nodes, Edges).
//
// On success the workflow's metadata.ready_marker is stamped with the
// simulation id and the caller's username so the promotion UI can show
// "Marked ready by X at Y". On failure the response describes which
// gate tripped so the agent (or the user) knows what to do next.
//
// This is intentionally an autonomous-tier tool: it has no side effect
// beyond stamping the metadata, and refusing it never blocks a human
// from promoting via the UI. The point is to give the agent a single
// place to assert completeness rather than letting it skip ahead to
// promote_workflow.
public sealed class MarkWorkflowReadyHandler : IToolHandler
{
    public string Name => "mark_workflow_ready";

    public string Description =>
        "Tier: autonomous. Assert that a draft workflow is verified and ready to promote. " +
        "The handler enforces that a fresh successful simulate_workflow_run exists for the " +
        "current DAG; if not, it returns ready=false with the blocker. Use this BEFORE " +
        "promote_workflow so the user has a single source of truth for completeness.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["workflow_id"],
          "properties": {
            "workflow_id": { "type": "string", "format": "uuid" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ISimulationResultRepository _simulations;
    private readonly ICurrentUser _caller;
    private readonly ILogger<MarkWorkflowReadyHandler> _logger;

    public MarkWorkflowReadyHandler(
        IRepository<WorkflowModel> workflows,
        ISimulationResultRepository simulations,
        ICurrentUser caller,
        ILogger<MarkWorkflowReadyHandler> logger)
    {
        _workflows = workflows;
        _simulations = simulations;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("workflow_id", out var widEl)
            || widEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(widEl.GetString(), out var workflowId))
        {
            _logger.LogWarning("ai.tool.mark_workflow_ready.validation_failed reason=workflow_id_required");
            return JsonSerializer.SerializeToElement(new { error = "workflow_id (uuid) is required" });
        }

        // Tracked + active-only — the handler stamps Metadata/UpdatedAt and
        // saves, matching the original FirstOrDefaultAsync (IsActive == true).
        var wf = await _workflows.GetByIdAsync(workflowId, activeOnly: true, tracking: true, ct);
        if (wf is null)
        {
            _logger.LogWarning("ai.tool.mark_workflow_ready.not_found workflow_id={WorkflowId}", workflowId);
            return JsonSerializer.SerializeToElement(new { error = "workflow not found" });
        }

        // The tool's `next_step` text talks about unblocking draft→qa
        // promotion, so calling it on a qa/production row is a category
        // error — refuse instead of silently re-stamping metadata. Edits
        // to qa/production rows are rejected by WorkflowService anyway.
        if (!string.Equals(wf.Environment, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return Block(workflowId, "not_a_draft",
                $"mark_workflow_ready only applies to draft workflows (current environment: {wf.Environment}).");
        }

        if (wf.LastSimulationId is null)
        {
            return Block(workflowId, "no_simulation",
                "no simulate_workflow_run has been recorded for this workflow. Call simulate_workflow_run first.");
        }

        var sim = await _simulations.FindActiveByIdAsync(wf.LastSimulationId.Value, ct);
        if (sim is null)
        {
            return Block(workflowId, "simulation_missing",
                "the workflow's last simulation row is gone. Re-run simulate_workflow_run.");
        }

        if (!sim.Ok)
        {
            return Block(workflowId, "simulation_failed",
                $"the last simulation reported {sim.IssueCount} issue(s). Fix the issues and re-simulate.");
        }

        var currentHash = Services.Workflow.WorkflowCanonicalizer.ComputeSchemaHash(wf.Nodes, wf.Edges);
        if (!string.Equals(currentHash, sim.SchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            return Block(workflowId, "simulation_stale",
                "the workflow has changed since the last simulation. Re-run simulate_workflow_run.");
        }

        // Stamp the marker. We merge into Metadata so we don't need a new
        // column for a value the promotion UI only reads as a hint.
        wf.Metadata = StampReadyMarker(wf.Metadata, sim.SimulationResultId, _caller.Username ?? string.Empty);
        wf.UpdatedAt = DateTime.UtcNow;
        var metadataStamped = true;
        try
        {
            await _workflows.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            metadataStamped = false;
            _logger.LogError(ex, "ai.tool.mark_workflow_ready.persist_failed workflow_id={WorkflowId}", workflowId);
            // Don't fail the tool — the gate decision (ready=true) is
            // what PromotionService cares about; the metadata stamp is
            // just a hint for the UI. We surface the failure to the
            // caller via `metadata_stamped` so the agent knows the UI
            // won't show the marker until a retry succeeds.
        }

        _logger.LogInformation(
            "ai.tool.mark_workflow_ready.ok workflow_id={WorkflowId} simulation_id={SimulationId} schema_hash={SchemaHash} metadata_stamped={MetadataStamped}",
            workflowId, sim.SimulationResultId, currentHash, metadataStamped);

        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = workflowId,
            ready = true,
            simulation_id = sim.SimulationResultId,
            schema_hash = currentHash,
            simulated_at = sim.SimulatedAt,
            metadata_stamped = metadataStamped,
            next_step = "promote_workflow draft→qa is now unblocked for this workflow.",
        });
    }

    private JsonElement Block(Guid workflowId, string reason, string message)
    {
        _logger.LogWarning(
            "ai.tool.mark_workflow_ready.blocked workflow_id={WorkflowId} reason={Reason}",
            workflowId, reason);
        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = workflowId,
            ready = false,
            blocker = reason,
            message,
        });
    }

    // Returns a new JsonElement that takes `existing` as a base and
    // overlays a `ready_marker` object. Avoids throwing on empty/null.
    private static JsonElement StampReadyMarker(JsonElement existing, Guid simulationId, string markedBy)
    {
        var marker = new
        {
            simulation_id = simulationId,
            marked_by = markedBy,
            marked_at = DateTime.UtcNow,
        };

        if (existing.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(new { ready_marker = marker });
        }

        // Rebuild as a Dictionary so we can overwrite ready_marker cleanly.
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in existing.EnumerateObject())
            dict[prop.Name] = prop.Value;
        dict["ready_marker"] = JsonSerializer.SerializeToElement(marker);
        return JsonSerializer.SerializeToElement(dict);
    }
}
