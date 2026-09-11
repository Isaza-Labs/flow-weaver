using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.IntegrationAction;

public class IntegrationActionService : IIntegrationAction
{
    private readonly IRepository<IntegrationActionModel> _actions;
    private readonly IRepository<IntegrationModel> _integrations;
    private readonly ICurrentUser _caller;
    private readonly ILogger<IntegrationActionService> _logger;

    public IntegrationActionService(
        IRepository<IntegrationActionModel> actions,
        IRepository<IntegrationModel> integrations,
        ICurrentUser caller,
        ILogger<IntegrationActionService> logger)
    {
        _actions = actions;
        _integrations = integrations;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<IntegrationActionResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _actions.CountAsync();
        var actions = await _actions.ListAsync(limit, offset);

        _logger.LogDebug(
            "integration_action.list.ok total={Total} returned={Returned}",
            total, actions.Count);

        return new OkObjectResult(new ListResponse<IntegrationActionResponse>
        {
            Data = actions.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<IntegrationActionResponse>> GetByIdAsync(Guid id)
    {
        var action = await _actions.GetByIdAsync(id);
        if (action is null)
        {
            _logger.LogWarning("integration_action.get.not_found integration_action_id={IntegrationActionId}", id);
            return new NotFoundObjectResult(new { error = "integration_action not found" });
        }

        return ToResponse(action);
    }

    public Task<ActionResult<IntegrationActionResponse>> PostAsync(CreateIntegrationAction dto)
    {
        _logger.LogWarning("integration_action.create.validation_failed reason=use_nested_route");
        return Task.FromResult<ActionResult<IntegrationActionResponse>>(
            new BadRequestObjectResult(new
            {
                error = "use POST /api/integration/{integrationId}/actions to create an integration action"
            }));
    }

    public async Task<ActionResult<IntegrationActionResponse>> PostForIntegrationAsync(Guid integrationId, CreateIntegrationAction dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("integration_action.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Method))
        {
            _logger.LogWarning("integration_action.create.validation_failed reason=method_required");
            return new BadRequestObjectResult(new { error = "method is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Path))
        {
            _logger.LogWarning("integration_action.create.validation_failed reason=path_required");
            return new BadRequestObjectResult(new { error = "path is required" });
        }

        // Parent integration must exist.
        var integrationExists = await _integrations.ExistsAsync(integrationId);
        if (!integrationExists)
        {
            _logger.LogWarning(
                "integration_action.create.not_found integration_id={IntegrationId} reason=parent_integration_missing",
                integrationId);
            return new NotFoundObjectResult(new { error = "integration not found" });
        }

        var action = new IntegrationActionModel
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = dto.Name,
            Description = dto.Description,
            Method = dto.Method.ToUpperInvariant(),
            Path = dto.Path,
            PathParams = dto.PathParams ?? default,
            QueryParams = dto.QueryParams ?? default,
            RequestBody = dto.RequestBody ?? default,
            ResponseSchema = dto.ResponseSchema ?? default,
            Category = dto.Category ?? string.Empty,
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _actions.Add(action);
        try
        {
            await _actions.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "integration_action.create.failed integration_action_name={IntegrationActionName}", action.Name);
            throw;
        }

        _logger.LogInformation(
            "integration_action.create.ok integration_action_id={IntegrationActionId} integration_action_name={IntegrationActionName} integration_id={IntegrationId}",
            action.IntegrationActionId, action.Name, integrationId);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "IntegrationAction",
            routeValues: new { id = action.IntegrationActionId },
            value: ToResponse(action));
    }

    public async Task<ActionResult<IntegrationActionResponse>> UpdateAsync(Guid id, UpdateIntegrationAction dto)
    {
        var action = await _actions.GetByIdAsync(id);
        if (action is null)
        {
            _logger.LogWarning("integration_action.update.not_found integration_action_id={IntegrationActionId}", id);
            return new NotFoundObjectResult(new { error = "integration_action not found" });
        }

        if (dto.Name is not null) action.Name = dto.Name;
        if (dto.Description is not null) action.Description = dto.Description;
        if (dto.Method is not null) action.Method = dto.Method.ToUpperInvariant();
        if (dto.Path is not null) action.Path = dto.Path;
        if (dto.PathParams is not null) action.PathParams = dto.PathParams.Value;
        if (dto.QueryParams is not null) action.QueryParams = dto.QueryParams.Value;
        if (dto.RequestBody is not null) action.RequestBody = dto.RequestBody.Value;
        if (dto.ResponseSchema is not null) action.ResponseSchema = dto.ResponseSchema.Value;
        if (dto.Category is not null) action.Category = dto.Category;
        if (dto.Enabled is not null) action.Enabled = dto.Enabled.Value;

        action.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _actions.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "integration_action.update.failed integration_action_id={IntegrationActionId}", action.IntegrationActionId);
            throw;
        }

        _logger.LogInformation("integration_action.update.ok integration_action_id={IntegrationActionId}", action.IntegrationActionId);
        return ToResponse(action);
    }

    // Soft delete: even though integration actions are child entities, we keep
    // them filterable from audit/reporting. Hard-delete (if needed) goes
    // through a purge job, not the public API.
    public async Task<ActionResult<IntegrationActionResponse>> DeleteAsync(Guid id)
    {
        var action = await _actions.GetByIdAsync(id);
        if (action is null)
        {
            _logger.LogWarning("integration_action.delete.not_found integration_action_id={IntegrationActionId}", id);
            return new NotFoundObjectResult(new { error = "integration_action not found" });
        }

        action.IsActive = false;
        action.UpdatedAt = DateTime.UtcNow;
        await _actions.SaveChangesAsync();

        _logger.LogInformation("integration_action.delete.ok integration_action_id={IntegrationActionId}", action.IntegrationActionId);
        return ToResponse(action);
    }

    private static IntegrationActionResponse ToResponse(IntegrationActionModel a) => new()
    {
        IntegrationActionId = a.IntegrationActionId,
        IntegrationId = a.IntegrationId,
        Name = a.Name,
        Description = a.Description,
        Method = a.Method,
        Path = a.Path,
        PathParams = a.PathParams,
        QueryParams = a.QueryParams,
        RequestBody = a.RequestBody,
        ResponseSchema = a.ResponseSchema,
        Category = a.Category,
        Enabled = a.Enabled,
        CreatedAt = a.CreatedAt,
    };
}
