using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using AIAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Services.AIAgent;

public class AIAgentService : IAIAgent
{
    private const int DefaultMaxIterations = 20;
    private const double DefaultTemperature = 0.2;

    private readonly IRepository<AIAgentModel> _agents;
    private readonly ICurrentUser _caller;
    private readonly ILogger<AIAgentService> _logger;

    public AIAgentService(IRepository<AIAgentModel> agents, ICurrentUser caller, ILogger<AIAgentService> logger)
    {
        _agents = agents;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<AIAgentResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _agents.CountAsync();
        var agents = await _agents.ListAsync(limit, offset);

        _logger.LogDebug(
            "ai_agent.list.ok total={Total} returned={Returned}",
            total, agents.Count);

        return new OkObjectResult(new ListResponse<AIAgentResponse>
        {
            Data = agents.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<AIAgentResponse>> GetByIdAsync(Guid id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent is null)
        {
            _logger.LogWarning("ai_agent.get.not_found ai_agent_id={AIAgentId}", id);
            return new NotFoundObjectResult(new { error = "ai_agent not found" });
        }

        return ToResponse(agent);
    }

    public async Task<ActionResult<AIAgentResponse>> PostAsync(CreateAIAgent dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("ai_agent.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Role))
        {
            _logger.LogWarning("ai_agent.create.validation_failed reason=role_required");
            return new BadRequestObjectResult(new { error = "role is required" });
        }

        var now = DateTime.UtcNow;
        var agent = new AIAgentModel
        {
            AIAgentId = Guid.NewGuid(),
            Name = dto.Name,
            Role = dto.Role,
            Description = dto.Description,
            ProviderId = dto.ProviderId,
            ModelOverride = dto.ModelOverride,
            SystemPrompt = dto.SystemPrompt,
            Tools = dto.Tools ?? new(),
            MaxIterations = dto.MaxIterations ?? DefaultMaxIterations,
            Temperature = dto.Temperature ?? DefaultTemperature,
            Config = dto.Config ?? default,
            Enabled = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _agents.Add(agent);
        try
        {
            await _agents.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_agent.create.failed ai_agent_name={AIAgentName}", agent.Name);
            throw;
        }

        _logger.LogInformation(
            "ai_agent.create.ok ai_agent_id={AIAgentId} ai_agent_name={AIAgentName}",
            agent.AIAgentId, agent.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "AIAgent",
            routeValues: new { id = agent.AIAgentId },
            value: ToResponse(agent));
    }

    public async Task<ActionResult<AIAgentResponse>> UpdateAsync(Guid id, UpdateAIAgent dto)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent is null)
        {
            _logger.LogWarning("ai_agent.update.not_found ai_agent_id={AIAgentId}", id);
            return new NotFoundObjectResult(new { error = "ai_agent not found" });
        }

        if (dto.Name is not null) agent.Name = dto.Name;
        if (dto.Role is not null) agent.Role = dto.Role;
        if (dto.Description is not null) agent.Description = dto.Description;
        if (dto.ProviderId is not null) agent.ProviderId = dto.ProviderId;
        if (dto.ModelOverride is not null) agent.ModelOverride = dto.ModelOverride;
        if (dto.SystemPrompt is not null) agent.SystemPrompt = dto.SystemPrompt;
        if (dto.Tools is not null) agent.Tools = dto.Tools;
        if (dto.MaxIterations is not null) agent.MaxIterations = dto.MaxIterations.Value;
        if (dto.Temperature is not null) agent.Temperature = dto.Temperature.Value;
        if (dto.Config is not null) agent.Config = dto.Config.Value;
        if (dto.Enabled is not null) agent.Enabled = dto.Enabled.Value;

        agent.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _agents.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_agent.update.failed ai_agent_id={AIAgentId}", agent.AIAgentId);
            throw;
        }

        _logger.LogInformation("ai_agent.update.ok ai_agent_id={AIAgentId}", agent.AIAgentId);
        return ToResponse(agent);
    }

    public async Task<ActionResult<AIAgentResponse>> DeleteAsync(Guid id)
    {
        var agent = await _agents.GetByIdAsync(id);
        if (agent is null)
        {
            _logger.LogWarning("ai_agent.delete.not_found ai_agent_id={AIAgentId}", id);
            return new NotFoundObjectResult(new { error = "ai_agent not found" });
        }

        agent.IsActive = false;
        agent.UpdatedAt = DateTime.UtcNow;
        await _agents.SaveChangesAsync();

        _logger.LogInformation("ai_agent.delete.ok ai_agent_id={AIAgentId}", agent.AIAgentId);
        return ToResponse(agent);
    }

    private static AIAgentResponse ToResponse(AIAgentModel a) => new()
    {
        AIAgentId = a.AIAgentId,
        Name = a.Name,
        Role = a.Role,
        Description = a.Description,
        ProviderId = a.ProviderId,
        ModelOverride = a.ModelOverride,
        SystemPrompt = a.SystemPrompt,
        Tools = a.Tools,
        MaxIterations = a.MaxIterations,
        Temperature = a.Temperature,
        Config = a.Config,
        Enabled = a.Enabled,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt,
    };
}
