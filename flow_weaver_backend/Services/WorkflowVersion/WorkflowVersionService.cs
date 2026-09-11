using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowVersionModel = flow_weaver_backend.Models.WorkflowVersion;

namespace flow_weaver_backend.Services.WorkflowVersion;

public class WorkflowVersionService : IWorkflowVersion
{
    private readonly IWorkflowVersionRepository _versions;
    private readonly IRepository<WorkflowModel> _workflows;
    private readonly ICurrentUser _caller;
    private readonly ILogger<WorkflowVersionService> _logger;

    public WorkflowVersionService(
        IWorkflowVersionRepository versions,
        IRepository<WorkflowModel> workflows,
        ICurrentUser caller,
        ILogger<WorkflowVersionService> logger)
    {
        _versions = versions;
        _workflows = workflows;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<WorkflowVersionResponse>>> GetByWorkflowAsync(
        Guid workflowId, int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        // Parent workflow must exist before any version data is returned, so
        // an unknown workflow_id reports the workflow as missing rather than
        // an empty version list.
        var workflowExists = await _workflows.ExistsAsync(workflowId);
        if (!workflowExists)
        {
            _logger.LogWarning(
                "workflow_version.list.not_found workflow_id={WorkflowId} reason=parent_workflow_missing",
                workflowId);
            return new NotFoundObjectResult(new { error = "workflow not found" });
        }

        var total = await _versions.CountByWorkflowAsync(workflowId);
        var versions = await _versions.ListByWorkflowAsync(workflowId, limit, offset);

        _logger.LogDebug(
            "workflow_version.list.ok workflow_id={WorkflowId} total={Total} returned={Returned}",
            workflowId, total, versions.Count);

        return new OkObjectResult(new ListResponse<WorkflowVersionResponse>
        {
            Data = versions.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<WorkflowVersionResponse>> GetByIdAsync(Guid id)
    {
        var v = await _versions.GetByIdAsync(id, tracking: false);
        if (v is null)
        {
            _logger.LogWarning("workflow_version.get.not_found workflow_version_id={WorkflowVersionId}", id);
            return new NotFoundObjectResult(new { error = "workflow_version not found" });
        }

        return ToResponse(v);
    }

    private static WorkflowVersionResponse ToResponse(WorkflowVersionModel v) => new()
    {
        Id = v.WorkflowVersionId,
        WorkflowId = v.WorkflowId,
        Version = v.Version,
        Nodes = v.Nodes,
        Edges = v.Edges,
        Services = v.Services,
        ChangeSummary = v.ChangeSummary,
        PromotedAt = v.PromotedAt,
        ConversationId = v.ConversationId,
        PromotedBy = v.PromotedBy,
        TestRunId = v.TestRunId,
        CreatedAt = v.CreatedAt,
    };
}
