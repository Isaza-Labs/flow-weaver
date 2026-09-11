using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Validation;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Merges a new `config_overrides` object into one node's current config.
// Used by the "Edit with AI" flow so the agent can apply a fix without
// requiring the operator to copy-paste back into the workflow editor.
//
// Safety rails:
//   - Production workflows are immutable (same rule WorkflowService
//     enforces) — returns 409-ish error so the agent can suggest
//     promoting through draft → qa instead.
//   - Every mutation writes a domain audit row capturing before/after,
//     so the change is reviewable in /admin/audit.
//   - This handler does NOT create nodes, delete nodes, or mutate edges.
//     Scope creep here would let the agent restructure workflows silently.
public sealed class UpdateWorkflowNodeConfigHandler : IToolHandler
{
    public string Name => "update_workflow_node_config";
    public string Description =>
        "Tier: single_confirm. Merges a `config_overrides` object into one " +
        "node of one workflow. SHALLOW MERGE — pass ONLY the keys you want " +
        "to change, the rest are preserved. e.g. to fix an SSH command, " +
        "send `config_overrides: { command: \"show system lldp neighbor\" }` " +
        "and device_type/timeout/etc. stay as they were. Reference-validates " +
        "integration_action nodes (integration_id + action_id must be real " +
        "GUIDs) before persisting. Production workflows are " +
        "immutable — if the target is production, ask the user to clone to " +
        "draft first. Include this call inside the single batch Plan block; " +
        "do NOT emit a fresh Plan+confirm between this and the next tool call.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","format":"uuid"},
          "node_id":{"type":"string"},
          "config_overrides":{"type":"object","additionalProperties":true}
        },"required":["workflow_id","node_id","config_overrides"],"additionalProperties":false}
        """).RootElement;

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly IWorkflowReferenceValidator _referenceValidator;
    private readonly ILogger<UpdateWorkflowNodeConfigHandler> _logger;

    public UpdateWorkflowNodeConfigHandler(
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        IAuditLogger audit,
        IWorkflowReferenceValidator referenceValidator,
        ILogger<UpdateWorkflowNodeConfigHandler> logger)
    {
        _workflows = workflows;
        _caller = caller;
        _audit = audit;
        _referenceValidator = referenceValidator;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("workflow_id", out var wfIdEl)
            || !Guid.TryParse(wfIdEl.GetString(), out var workflowId))
        {
            _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=workflow_id_required");
            return Err("workflow_id (uuid) is required");
        }
        if (!args.TryGetProperty("node_id", out var nodeIdEl)
            || nodeIdEl.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=node_id_required");
            return Err("node_id is required");
        }
        if (!args.TryGetProperty("config_overrides", out var overridesEl))
        {
            _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=config_overrides_missing");
            return Err("config_overrides is required (object with the keys to merge — e.g. {\"command\": \"show version\"})");
        }
        // Lenient parsing: some LLMs serialize the inner object as a
        // string (`"config_overrides": "{\"command\":\"…\"}"`). Parse
        // the string in that case so the agent's first-pass call
        // succeeds instead of triggering a retry that costs a turn.
        if (overridesEl.ValueKind == JsonValueKind.String)
        {
            var raw = overridesEl.GetString() ?? string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=config_overrides_string_not_object");
                    return Err("config_overrides was a JSON string but didn't decode to an object — pass the merge keys as an object literal, not a JSON-encoded string");
                }
                overridesEl = JsonDocument.Parse(raw).RootElement;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=config_overrides_string_parse_failed err={Error}", ex.Message);
                return Err($"config_overrides looked like a JSON string but didn't parse: {ex.Message}");
            }
        }
        if (overridesEl.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("ai.tool.update_workflow_node_config.validation_failed reason=config_overrides_wrong_type kind={Kind}", overridesEl.ValueKind);
            return Err($"config_overrides must be a JSON object (got {overridesEl.ValueKind}). Pass the merge keys directly, e.g. {{\"command\": \"show version\"}}");
        }

        var nodeId = nodeIdEl.GetString()!;
        _logger.LogDebug(
            "ai.tool.update_workflow_node_config.start workflow_id={WorkflowId} node_id={NodeId}",
            workflowId, nodeId);

        try
        {
            // Tracked + active-only — the handler mutates Nodes/UpdatedAt and
            // saves, matching the original FirstOrDefaultAsync (no AsNoTracking,
            // IsActive == true).
            var workflow = await _workflows.GetByIdAsync(workflowId, activeOnly: true, tracking: true, ct);
            if (workflow is null)
            {
                _logger.LogWarning(
                    "ai.tool.update_workflow_node_config.not_found workflow_id={WorkflowId}",
                    workflowId);
                return Err("workflow not found");
            }

            if (string.Equals(workflow.Environment, "production", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "ai.tool.update_workflow_node_config.validation_failed reason=production_immutable workflow_id={WorkflowId}",
                    workflowId);
                return Err("workflow is in production (immutable) — clone to draft first");
            }

            if (workflow.Nodes.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "ai.tool.update_workflow_node_config.validation_failed reason=nodes_not_array workflow_id={WorkflowId}",
                    workflowId);
                return Err("workflow has no nodes array");
            }

            var (merged, before, after, found) = MergeNodeConfig(workflow.Nodes, nodeId, overridesEl);
            if (!found)
            {
                _logger.LogWarning(
                    "ai.tool.update_workflow_node_config.not_found workflow_id={WorkflowId} node_id={NodeId}",
                    workflowId, nodeId);
                return Err($"node '{nodeId}' not found in workflow");
            }

            // Same references pass that WorkflowService runs on create/update:
            // reject bogus integration_id / action_id GUIDs *before* persisting
            // the merged config. Without this gate the agent could overwrite a
            // valid node with a placeholder and only discover the mistake at
            // the next run.
            var referenceResult = await _referenceValidator.ValidateAsync(merged, ct);
            if (!referenceResult.IsValid)
            {
                _logger.LogWarning(
                    "ai.tool.update_workflow_node_config.validation_failed reason=references_invalid workflow_id={WorkflowId} node_id={NodeId}",
                    workflowId, nodeId);
                return JsonSerializer.SerializeToElement(new
                {
                    error = "references_invalid",
                    details = referenceResult.Errors,
                });
            }

            workflow.Nodes = merged;
            workflow.UpdatedAt = DateTime.UtcNow;
            await _workflows.SaveChangesAsync(ct);

            await _audit.LogAsync(
                "workflow", workflow.WorkflowId, "update_node_config",
                new { node_id = nodeId, config_overrides = before },
                new { node_id = nodeId, config_overrides = after },
                ct);

            _logger.LogInformation(
                "ai.tool.update_workflow_node_config.ok workflow_id={WorkflowId} node_id={NodeId}",
                workflowId, nodeId);

            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                workflow_id = workflow.WorkflowId,
                node_id = nodeId,
                config_overrides_before = before,
                config_overrides_after = after,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ai.tool.update_workflow_node_config.failed workflow_id={WorkflowId} node_id={NodeId}",
                workflowId, nodeId);
            throw;
        }
    }

    private static JsonElement Err(string msg) =>
        JsonSerializer.SerializeToElement(new { error = msg });

    // Rebuilds the Nodes array with the target node's config_overrides
    // shallowly merged against `incoming`. Returns the new array plus the
    // before/after snapshots of just that node's config for the audit log.
    private static (JsonElement merged, JsonElement before, JsonElement after, bool found) MergeNodeConfig(
        JsonElement nodes, string nodeId, JsonElement incoming)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            JsonElement beforeSnapshot = default;
            JsonElement afterSnapshot = default;
            var found = false;

            foreach (var node in nodes.EnumerateArray())
            {
                var isTarget = node.ValueKind == JsonValueKind.Object
                    && node.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String
                    && idEl.GetString() == nodeId;

                if (!isTarget)
                {
                    node.WriteTo(writer);
                    continue;
                }

                found = true;
                var existing = node.TryGetProperty("config_overrides", out var coEl)
                               && coEl.ValueKind == JsonValueKind.Object
                    ? coEl
                    : JsonDocument.Parse("{}").RootElement;
                beforeSnapshot = JsonDocument.Parse(existing.GetRawText()).RootElement;

                // Shallow merge: incoming keys win.
                var merged = new Dictionary<string, JsonElement>();
                foreach (var prop in existing.EnumerateObject())
                    merged[prop.Name] = prop.Value;
                foreach (var prop in incoming.EnumerateObject())
                    merged[prop.Name] = prop.Value;

                writer.WriteStartObject();
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Name == "config_overrides") continue;
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                writer.WritePropertyName("config_overrides");
                writer.WriteStartObject();
                foreach (var kv in merged)
                {
                    writer.WritePropertyName(kv.Key);
                    kv.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();

                afterSnapshot = JsonSerializer.SerializeToElement(merged);
            }

            writer.WriteEndArray();
            writer.Flush();

            var rebuilt = JsonDocument.Parse(ms.ToArray()).RootElement;
            return (rebuilt, beforeSnapshot, afterSnapshot, found);
        }
    }
}
