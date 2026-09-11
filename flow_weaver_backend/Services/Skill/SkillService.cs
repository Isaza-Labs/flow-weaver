using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using SkillModel = flow_weaver_backend.Models.Skill;

namespace flow_weaver_backend.Services.Skill;

public class SkillService : ISkill
{
    private readonly IRepository<SkillModel> _skills;
    private readonly ICurrentUser _caller;
    private readonly ILogger<SkillService> _logger;

    public SkillService(IRepository<SkillModel> skills, ICurrentUser caller, ILogger<SkillService> logger)
    {
        _skills = skills;
        _caller = caller;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<SkillResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _skills.CountAsync();
        var skills = await _skills.ListAsync(limit, offset);

        _logger.LogDebug(
            "skill.list.ok total={Total} returned={Returned}",
            total, skills.Count);

        return new OkObjectResult(new ListResponse<SkillResponse>
        {
            Data = skills.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<SkillResponse>> GetByIdAsync(Guid id)
    {
        var skill = await _skills.GetByIdAsync(id);
        if (skill is null)
        {
            _logger.LogWarning("skill.get.not_found skill_id={SkillId}", id);
            return new NotFoundObjectResult(new { error = "skill not found" });
        }

        return ToResponse(skill);
    }

    public async Task<ActionResult<SkillResponse>> PostAsync(CreateSkill dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("skill.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.SkillType))
        {
            _logger.LogWarning("skill.create.validation_failed reason=skill_type_required");
            return new BadRequestObjectResult(new { error = "skill_type is required" });
        }

        var now = DateTime.UtcNow;
        var skill = new SkillModel
        {
            SkillId = Guid.NewGuid(),
            Name = dto.Name,
            Triggers = dto.Triggers ?? new(),
            Description = dto.Description,
            SkillType = dto.SkillType,
            ActionConfig = dto.ActionConfig,
            ParameterMapping = dto.ParameterMapping ?? default,
            Examples = dto.Examples ?? default,
            Template = dto.Template ?? default,
            Parameters = dto.Parameters ?? default,
            Enabled = dto.Enabled ?? true,
            ConfirmationRequired = dto.ConfirmationRequired ?? false,
            LearnedFrom = string.Empty,
            UseCount = 0,
            SuccessCount = 0,
            Tags = dto.Tags ?? new(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _skills.Add(skill);
        try
        {
            await _skills.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "skill.create.failed skill_name={SkillName}", skill.Name);
            throw;
        }

        _logger.LogInformation(
            "skill.create.ok skill_id={SkillId} skill_name={SkillName}",
            skill.SkillId, skill.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "Skill",
            routeValues: new { id = skill.SkillId },
            value: ToResponse(skill));
    }

    public async Task<ActionResult<SkillResponse>> UpdateAsync(Guid id, UpdateSkill dto)
    {
        var skill = await _skills.GetByIdAsync(id);
        if (skill is null)
        {
            _logger.LogWarning("skill.update.not_found skill_id={SkillId}", id);
            return new NotFoundObjectResult(new { error = "skill not found" });
        }

        if (dto.Name is not null) skill.Name = dto.Name;
        if (dto.Triggers is not null) skill.Triggers = dto.Triggers;
        if (dto.Description is not null) skill.Description = dto.Description;
        if (dto.SkillType is not null) skill.SkillType = dto.SkillType;
        if (dto.ActionConfig is not null) skill.ActionConfig = dto.ActionConfig.Value;
        if (dto.ParameterMapping is not null) skill.ParameterMapping = dto.ParameterMapping.Value;
        if (dto.Examples is not null) skill.Examples = dto.Examples.Value;
        if (dto.Template is not null) skill.Template = dto.Template.Value;
        if (dto.Parameters is not null) skill.Parameters = dto.Parameters.Value;
        if (dto.Enabled is not null) skill.Enabled = dto.Enabled.Value;
        if (dto.ConfirmationRequired is not null) skill.ConfirmationRequired = dto.ConfirmationRequired.Value;
        if (dto.Tags is not null) skill.Tags = dto.Tags;

        skill.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _skills.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "skill.update.failed skill_id={SkillId}", skill.SkillId);
            throw;
        }

        _logger.LogInformation("skill.update.ok skill_id={SkillId}", skill.SkillId);
        return ToResponse(skill);
    }

    public async Task<ActionResult<SkillResponse>> DeleteAsync(Guid id)
    {
        var skill = await _skills.GetByIdAsync(id);
        if (skill is null)
        {
            _logger.LogWarning("skill.delete.not_found skill_id={SkillId}", id);
            return new NotFoundObjectResult(new { error = "skill not found" });
        }

        skill.IsActive = false;
        skill.UpdatedAt = DateTime.UtcNow;
        await _skills.SaveChangesAsync();

        _logger.LogInformation("skill.delete.ok skill_id={SkillId}", skill.SkillId);
        return ToResponse(skill);
    }

    private static SkillResponse ToResponse(SkillModel s) => new()
    {
        SkillId = s.SkillId,
        Name = s.Name,
        Triggers = s.Triggers,
        Description = s.Description,
        SkillType = s.SkillType,
        ActionConfig = s.ActionConfig,
        ParameterMapping = s.ParameterMapping,
        Examples = s.Examples,
        Template = s.Template,
        Parameters = s.Parameters,
        Enabled = s.Enabled,
        ConfirmationRequired = s.ConfirmationRequired,
        LearnedFrom = s.LearnedFrom,
        UseCount = s.UseCount,
        SuccessCount = s.SuccessCount,
        LastUsedAt = s.LastUsedAt,
        Tags = s.Tags,
        CreatedBy = s.CreatedBy,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
