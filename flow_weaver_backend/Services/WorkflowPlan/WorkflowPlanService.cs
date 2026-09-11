using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using WorkflowPlanModel = flow_weaver_backend.Models.WorkflowPlan;

namespace flow_weaver_backend.Services.WorkflowPlan;

public class WorkflowPlanService : IWorkflowPlan
{
    private readonly IRepository<WorkflowPlanModel> _plans;
    private readonly ISnippetRepository _snippets;
    private readonly IRepository<Models.Workflow> _workflows;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _caller;
    private readonly ILogger<WorkflowPlanService> _logger;

    public WorkflowPlanService(
        IRepository<WorkflowPlanModel> plans,
        ISnippetRepository snippets,
        IRepository<Models.Workflow> workflows,
        IUnitOfWork uow,
        ICurrentUser caller,
        ILogger<WorkflowPlanService> logger)
    {
        _plans = plans;
        _snippets = snippets;
        _workflows = workflows;
        _uow = uow;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<WorkflowPlanResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _plans.CountAsync();
        var plans = await _plans.ListAsync(limit, offset);

        _logger.LogDebug(
            "workflow_plan.list.ok total={Total} returned={Returned}",
            total, plans.Count);

        return new OkObjectResult(new ListResponse<WorkflowPlanResponse>
        {
            Data = plans.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<WorkflowPlanResponse>> GetByIdAsync(Guid id)
    {
        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.get.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> PostAsync(CreateWorkflowPlan dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Intent))
        {
            _logger.LogWarning("workflow_plan.create.validation_failed reason=intent_required");
            return new BadRequestObjectResult(new { error = "intent is required" });
        }

        var now = DateTime.UtcNow;
        var plan = new WorkflowPlanModel
        {
            WorkflowPlanId = Guid.NewGuid(),
            ConversationId = dto.ConversationId,
            Intent = dto.Intent,
            Description = dto.Description,
            Steps = dto.Steps ?? default,
            ServicesToCreate = dto.ServicesToCreate ?? default,
            ServicesToReuse = dto.ServicesToReuse ?? default,
            TargetDevices = dto.TargetDevices ?? new(),
            TargetPools = dto.TargetPools ?? new(),
            Risks = dto.Risks ?? default,
            Status = PlanStatus.Draft,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _plans.Add(plan);
        try
        {
            await _plans.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow_plan.create.failed intent={Intent}", plan.Intent);
            throw;
        }

        _logger.LogInformation(
            "workflow_plan.create.ok workflow_plan_id={WorkflowPlanId} intent={Intent}",
            plan.WorkflowPlanId, plan.Intent);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "WorkflowPlan",
            routeValues: new { id = plan.WorkflowPlanId },
            value: ToResponse(plan));
    }

    public async Task<ActionResult<WorkflowPlanResponse>> UpdateAsync(Guid id, UpdateWorkflowPlan dto)
    {
        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.update.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status != PlanStatus.Draft)
        {
            _logger.LogWarning(
                "workflow_plan.update.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
            {
                error = $"plan is in status '{plan.Status}'; only drafts are editable"
            });
        }

        if (dto.Intent is not null) plan.Intent = dto.Intent;
        if (dto.Description is not null) plan.Description = dto.Description;
        if (dto.Steps is not null) plan.Steps = dto.Steps.Value;
        if (dto.ServicesToCreate is not null) plan.ServicesToCreate = dto.ServicesToCreate.Value;
        if (dto.ServicesToReuse is not null) plan.ServicesToReuse = dto.ServicesToReuse.Value;
        if (dto.TargetDevices is not null) plan.TargetDevices = dto.TargetDevices;
        if (dto.TargetPools is not null) plan.TargetPools = dto.TargetPools;
        if (dto.Risks is not null) plan.Risks = dto.Risks.Value;

        plan.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _plans.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow_plan.update.failed workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);
            throw;
        }

        _logger.LogInformation("workflow_plan.update.ok workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);
        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> DeleteAsync(Guid id)
    {
        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.delete.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status is PlanStatus.Built or PlanStatus.Building or PlanStatus.Approved)
        {
            _logger.LogWarning(
                "workflow_plan.delete.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
            {
                error = $"plan in status '{plan.Status}' cannot be deleted"
            });
        }

        plan.IsActive = false;
        plan.UpdatedAt = DateTime.UtcNow;
        await _plans.SaveChangesAsync();

        _logger.LogInformation("workflow_plan.delete.ok workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);
        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> SubmitForApprovalAsync(Guid id)
    {
        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.submit.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status != PlanStatus.Draft)
        {
            _logger.LogWarning(
                "workflow_plan.submit.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
            {
                error = $"only plans in 'draft' can be submitted; current status is '{plan.Status}'"
            });
        }

        plan.Status = PlanStatus.AwaitingApproval;
        plan.UpdatedAt = DateTime.UtcNow;
        await _plans.SaveChangesAsync();

        _logger.LogInformation("workflow_plan.submit.ok workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);
        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> ApproveAsync(Guid id, ApprovePlan dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ApprovedBy))
        {
            _logger.LogWarning("workflow_plan.approve.validation_failed reason=approved_by_required");
            return new BadRequestObjectResult(new { error = "approved_by is required" });
        }

        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.approve.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status != PlanStatus.AwaitingApproval)
        {
            _logger.LogWarning(
                "workflow_plan.approve.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
            {
                error = $"only plans in 'awaiting_approval' can be approved; current status is '{plan.Status}'"
            });
        }

        plan.Status = PlanStatus.Approved;
        plan.ApprovedBy = dto.ApprovedBy;
        plan.ApprovedAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;
        await _plans.SaveChangesAsync();

        _logger.LogInformation(
            "workflow_plan.approve.ok workflow_plan_id={WorkflowPlanId} approved_by={ApprovedBy}",
            plan.WorkflowPlanId, plan.ApprovedBy);
        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> RejectAsync(Guid id, RejectPlan dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ApprovedBy))
        {
            _logger.LogWarning("workflow_plan.reject.validation_failed reason=approved_by_required");
            return new BadRequestObjectResult(new { error = "approved_by is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            _logger.LogWarning("workflow_plan.reject.validation_failed reason=reason_required");
            return new BadRequestObjectResult(new { error = "reason is required" });
        }

        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.reject.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status != PlanStatus.AwaitingApproval)
        {
            _logger.LogWarning(
                "workflow_plan.reject.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
            {
                error = $"only plans in 'awaiting_approval' can be rejected; current status is '{plan.Status}'"
            });
        }

        plan.Status = PlanStatus.Rejected;
        plan.RejectionReason = dto.Reason;
        plan.ApprovedBy = dto.ApprovedBy;
        plan.UpdatedAt = DateTime.UtcNow;
        await _plans.SaveChangesAsync();

        _logger.LogInformation(
            "workflow_plan.reject.ok workflow_plan_id={WorkflowPlanId} approved_by={ApprovedBy}",
            plan.WorkflowPlanId, plan.ApprovedBy);
        return ToResponse(plan);
    }

    public async Task<ActionResult<WorkflowPlanResponse>> BuildAsync(Guid id)
    {
        var plan = await LoadAsync(id);
        if (plan is null)
        {
            _logger.LogWarning("workflow_plan.build.not_found workflow_plan_id={WorkflowPlanId}", id);
            return new NotFoundObjectResult(new { error = "workflow_plan not found" });
        }

        if (plan.Status != PlanStatus.Approved)
        {
            _logger.LogWarning(
                "workflow_plan.build.conflict workflow_plan_id={WorkflowPlanId} status={Status}",
                plan.WorkflowPlanId, plan.Status);
            return new ConflictObjectResult(new
                { error = $"only approved plans can be built; current status is '{plan.Status}'" });
        }

        plan.Status = PlanStatus.Building;
        plan.UpdatedAt = DateTime.UtcNow;
        await _uow.SaveChangesAsync();

        _logger.LogDebug("workflow_plan.build.start workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);

        try
        {
            // 1. Create missing Snippets from services_to_create.
            var svcIdMap = await CreateSnippetsAsync(plan);

            // 2. Build Workflow.Nodes + Edges from plan.Steps.
            var (nodes, edges) = BuildGraph(plan, svcIdMap);

            // 3. Create the Workflow.
            var now = DateTime.UtcNow;
            var wf = new Models.Workflow
            {
                WorkflowId = Guid.NewGuid(),
                Name = plan.Intent,
                Description = plan.Description,
                Version = 1,
                SchemaVersion = "v1",
                InputSchema = default,
                Nodes = nodes,
                Edges = edges,
                Metadata = default,
                Environment = "draft",
                ChangeSummary = $"Built from plan {plan.WorkflowPlanId}",
                ConversationId = plan.ConversationId,
                // The user who triggered the build (plan approval + build run
                // in an authenticated scope — chat tool or HTTP). Was null.
                CreatedBy = _caller.Username,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _workflows.Add(wf);

            plan.Status = PlanStatus.Built;
            plan.WorkflowId = wf.WorkflowId;
            plan.ExecutedAt = DateTime.UtcNow;
            plan.UpdatedAt = DateTime.UtcNow;
            await _uow.SaveChangesAsync();

            _logger.LogInformation(
                "workflow_plan.build.ok workflow_plan_id={WorkflowPlanId} workflow_id={WorkflowId}",
                plan.WorkflowPlanId, wf.WorkflowId);

            return ToResponse(plan);
        }
        catch (Exception ex)
        {
            plan.Status = PlanStatus.Failed;
            plan.UpdatedAt = DateTime.UtcNow;
            await _uow.SaveChangesAsync();
            _logger.LogError(ex, "workflow_plan.build.failed workflow_plan_id={WorkflowPlanId}", plan.WorkflowPlanId);
            return new ObjectResult(new { error = $"build failed: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    private async Task<Dictionary<string, Guid>> CreateSnippetsAsync(WorkflowPlanModel plan)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (plan.ServicesToCreate.ValueKind != System.Text.Json.JsonValueKind.Array) return map;

        foreach (var svcEl in plan.ServicesToCreate.EnumerateArray())
        {
            var name = svcEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type = svcEl.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) continue;

            var existing = await _snippets.FindActiveByNameAsync(name);
            if (existing is not null) { map[name] = existing.SnippetId; continue; }

            var now = DateTime.UtcNow;
            var svc = new Models.Snippet
            {
                SnippetId = Guid.NewGuid(),
                Name = name,
                Type = type,
                Description = svcEl.TryGetProperty("description", out var d) ? d.GetString() : null,
                TargetMode = "fan_out",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _snippets.Add(svc);
            map[name] = svc.SnippetId;
        }
        await _uow.SaveChangesAsync();
        return map;
    }

    private static (System.Text.Json.JsonElement nodes, System.Text.Json.JsonElement edges) BuildGraph(
        WorkflowPlanModel plan, Dictionary<string, Guid> svcIdMap)
    {
        var nodeList = new List<object>();
        var edgeList = new List<object>();
        var prevId = "__start__";
        var yPos = 0;

        nodeList.Add(new { id = "__start__", snippet_id = "__start__", x = 200, y = yPos });
        yPos += 120;

        if (plan.Steps.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var step in plan.Steps.EnumerateArray())
            {
                var stepName = step.TryGetProperty("name", out var sn) ? sn.GetString() ?? "step" : "step";
                var svcType = step.TryGetProperty("service_type", out var st) ? st.GetString() ?? "" : "";
                var nodeId = $"node-{nodeList.Count}";

                string snippetId;
                if (svcIdMap.TryGetValue(stepName, out var mapped))
                    snippetId = mapped.ToString();
                else if (svcIdMap.TryGetValue(svcType, out var byType))
                    snippetId = byType.ToString();
                else
                    snippetId = svcType;

                var config = step.TryGetProperty("config", out var c) ? (object)c : new { };

                nodeList.Add(new { id = nodeId, snippet_id = snippetId, x = 200, y = yPos, config_overrides = config });
                edgeList.Add(new { source = prevId, target = nodeId, type = "success" });
                prevId = nodeId;
                yPos += 120;
            }
        }

        nodeList.Add(new { id = "__end__", snippet_id = "__end__", x = 200, y = yPos });
        edgeList.Add(new { source = prevId, target = "__end__", type = "success" });

        return (
            System.Text.Json.JsonSerializer.SerializeToElement(nodeList),
            System.Text.Json.JsonSerializer.SerializeToElement(edgeList));
    }

    // Shared by every method that resolves a plan by id.
    private Task<WorkflowPlanModel?> LoadAsync(Guid id) =>
        _plans.GetByIdAsync(id);

    private static WorkflowPlanResponse ToResponse(WorkflowPlanModel p) => new()
    {
        WorkflowPlanId = p.WorkflowPlanId,
        ConversationId = p.ConversationId,
        Intent = p.Intent,
        Description = p.Description,
        Steps = p.Steps,
        ServicesToCreate = p.ServicesToCreate,
        ServicesToReuse = p.ServicesToReuse,
        TargetDevices = p.TargetDevices,
        TargetPools = p.TargetPools,
        Risks = p.Risks,
        Status = p.Status,
        RejectionReason = p.RejectionReason,
        ApprovedBy = p.ApprovedBy,
        ApprovedAt = p.ApprovedAt,
        ExecutedAt = p.ExecutedAt,
        WorkflowId = p.WorkflowId,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
