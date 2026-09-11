using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Full DAG for one workflow id: nodes, edges, environment, input schema.
// The "Edit with AI" flow uses this to learn the current graph before
// calling `update_workflow_node_config` to mutate one node.
public sealed class GetWorkflowDetailsHandler : IToolHandler
{
    public string Name => "get_workflow_details";
    public string Description =>
        "Returns the full DAG (nodes + edges), environment, and input schema " +
        "for one workflow. Call before `update_workflow_node_config` so you " +
        "know the exact node ids and their current `config_overrides`.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","format":"uuid"}
        },"required":["workflow_id"],"additionalProperties":false}
        """).RootElement;

    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly IAppLinks _links;
    private readonly ILogger<GetWorkflowDetailsHandler> _logger;

    public GetWorkflowDetailsHandler(
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        IAppLinks links,
        ILogger<GetWorkflowDetailsHandler> logger)
    {
        _workflows = workflows;
        _caller = caller;
        _links = links;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("workflow_id", out var idEl)
            || !Guid.TryParse(idEl.GetString(), out var workflowId))
        {
            _logger.LogWarning("ai.tool.get_workflow_details.validation_failed reason=workflow_id_required");
            return JsonSerializer.SerializeToElement(new { error = "workflow_id (uuid) is required" });
        }

        _logger.LogDebug("ai.tool.get_workflow_details.start workflow_id={WorkflowId}", workflowId);

        try
        {
            // No IsActive filter — mirrors the original query, which matched on
            // (WorkflowId) alone regardless of soft-delete state.
            var wf = await _workflows.GetByIdAsync(workflowId, activeOnly: false, tracking: false, ct);
            if (wf is null)
            {
                _logger.LogWarning("ai.tool.get_workflow_details.not_found workflow_id={WorkflowId}", workflowId);
                return JsonSerializer.SerializeToElement(new { error = "workflow not found" });
            }

            _logger.LogInformation(
                "ai.tool.get_workflow_details.ok workflow_id={WorkflowId} environment={Environment} version={Version}",
                workflowId, wf.Environment, wf.Version);

            return JsonSerializer.SerializeToElement(new
            {
                workflow_id = wf.WorkflowId,
                // The page an operator opens to look at this workflow. Put it
                // in the answer as a markdown link — a uuid alone makes the
                // user go hunting for it.
                url = _links.Workflow(wf.WorkflowId),
                name = wf.Name,
                description = wf.Description,
                version = wf.Version,
                environment = wf.Environment,
                schema_version = wf.SchemaVersion,
                input_schema = wf.InputSchema,
                nodes = wf.Nodes,
                edges = wf.Edges,
                metadata = wf.Metadata,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.get_workflow_details.failed workflow_id={WorkflowId}", workflowId);
            throw;
        }
    }
}
