using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;

namespace flow_weaver_backend.Services.AiPromptSkill;

// Admin-only CRUD over ai_prompt_skills rows. Every mutation invalidates
// the SkillPromptLoader cache so the next /api/ai/catalog/prompt request
// reflects the change without any hot-reload delay.
public class AiPromptSkillService : IAiPromptSkill
{
    private const int MaxContentBytes = 512 * 1024;

    // Matches the filenames we shipped as defaults (base.md, awx.md, …)
    // plus operator-invented names using letters, digits, underscore,
    // hyphen. The .md suffix is enforced — it keeps the UI consistent
    // with the skill-file convention and blocks obvious misuse like
    // dropping in a .yaml on the wrong endpoint.
    private static readonly Regex NamePattern =
        new(@"^[a-zA-Z0-9_\-]+\.md$", RegexOptions.Compiled);

    private readonly IAiPromptSkillRepository _skills;
    private readonly ICurrentUser _caller;
    private readonly ISkillPromptLoader _loader;
    private readonly ILogger<AiPromptSkillService> _logger;

    public AiPromptSkillService(
        IAiPromptSkillRepository skills,
        ICurrentUser caller,
        ISkillPromptLoader loader,
        ILogger<AiPromptSkillService> logger)
    {
        _skills = skills;
        _caller = caller;
        _loader = loader;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<AiPromptSkillResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _skills.CountAsync();
        var rows = await _skills.ListOrderedAsync(limit, offset);

        _logger.LogDebug(
            "ai_prompt_skill.list.ok total={Total} returned={Returned}",
            total, rows.Count);

        return new OkObjectResult(new ListResponse<AiPromptSkillResponse>
        {
            // List endpoint leaves Content empty so the payload stays small
            // even with dozens of skills; the UI fetches the body on demand
            // via GetById.
            Data = rows.Select(s => ToResponse(s, includeContent: false)).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<AiPromptSkillResponse>> GetByIdAsync(Guid id)
    {
        var row = await _skills.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_prompt_skill.get.not_found ai_prompt_skill_id={AiPromptSkillId}", id);
            return new NotFoundObjectResult(new { error = "prompt skill not found" });
        }

        return ToResponse(row, includeContent: true);
    }

    public async Task<ActionResult<AiPromptSkillResponse>> PostAsync(CreateAiPromptSkill dto)
    {
        var validation = ValidatePayload(dto.Name, dto.Content);
        if (validation is not null)
        {
            _logger.LogWarning("ai_prompt_skill.create.validation_failed reason=payload_invalid");
            return validation;
        }

        var name = dto.Name.Trim();

        // Upsert semantics: if an inactive row with the same name exists
        // (previously soft-deleted), reactivate + overwrite instead of
        // creating a duplicate the (Name) unique index would reject.
        // Frontend users see a single logical slot per name.
        var existing = await _skills.FindByNameAsync(name);

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            if (existing.IsActive)
            {
                _logger.LogWarning("ai_prompt_skill.create.conflict ai_prompt_skill_name={Name}", name);
                return new ConflictObjectResult(new { error = "prompt skill with that name already exists" });
            }

            existing.Content = dto.Content;
            existing.SortOrder = dto.SortOrder ?? DefaultSortOrder(name);
            existing.IsActive = true;
            existing.IntegrationId = dto.IntegrationId;
            existing.CreatedBy = _caller.UserId;
            existing.UpdatedAt = now;
            try
            {
                await _skills.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ai_prompt_skill.create.failed ai_prompt_skill_name={Name}", name);
                throw;
            }
            _loader.Invalidate();

            _logger.LogInformation(
                "ai_prompt_skill.create.ok ai_prompt_skill_id={AiPromptSkillId} ai_prompt_skill_name={Name}",
                existing.AiPromptSkillId, existing.Name);

            return ToResponse(existing, includeContent: true);
        }

        var skill = new PromptSkillModel
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = name,
            Content = dto.Content,
            SortOrder = dto.SortOrder ?? DefaultSortOrder(name),
            IntegrationId = dto.IntegrationId,
            CreatedBy = _caller.UserId,
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
            _logger.LogError(ex, "ai_prompt_skill.create.failed ai_prompt_skill_name={Name}", name);
            throw;
        }
        _loader.Invalidate();

        _logger.LogInformation(
            "ai_prompt_skill.create.ok ai_prompt_skill_id={AiPromptSkillId} ai_prompt_skill_name={Name}",
            skill.AiPromptSkillId, skill.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "AiPromptSkill",
            routeValues: new { id = skill.AiPromptSkillId },
            value: ToResponse(skill, includeContent: true));
    }

    public async Task<ActionResult<AiPromptSkillResponse>> UpdateAsync(Guid id, UpdateAiPromptSkill dto)
    {
        var row = await _skills.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_prompt_skill.update.not_found ai_prompt_skill_id={AiPromptSkillId}", id);
            return new NotFoundObjectResult(new { error = "prompt skill not found" });
        }

        if (dto.Name is not null)
        {
            var newName = dto.Name.Trim();
            if (!NamePattern.IsMatch(newName))
            {
                _logger.LogWarning("ai_prompt_skill.update.validation_failed reason=name_pattern");
                return new BadRequestObjectResult(new { error = "name must match ^[a-zA-Z0-9_\\-]+\\.md$" });
            }

            if (!string.Equals(newName, row.Name, StringComparison.Ordinal))
            {
                var clash = await _skills.NameExistsForOtherAsync(newName, id);
                if (clash)
                {
                    _logger.LogWarning(
                        "ai_prompt_skill.update.conflict ai_prompt_skill_id={AiPromptSkillId} ai_prompt_skill_name={Name}",
                        id, newName);
                    return new ConflictObjectResult(new { error = "another prompt skill uses that name" });
                }
                row.Name = newName;
            }
        }

        if (dto.Content is not null)
        {
            if (dto.Content.Length > MaxContentBytes)
            {
                _logger.LogWarning("ai_prompt_skill.update.validation_failed reason=content_too_large");
                return new BadRequestObjectResult(new { error = $"content exceeds {MaxContentBytes} bytes" });
            }
            row.Content = dto.Content;
        }

        if (dto.SortOrder is not null) row.SortOrder = dto.SortOrder.Value;
        if (dto.IsActive is not null) row.IsActive = dto.IsActive.Value;
        // Authoritative: the admin edit form always sends the picker value, so
        // null unlinks. No other caller PATCHes this endpoint partially.
        row.IntegrationId = dto.IntegrationId;

        row.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _skills.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_prompt_skill.update.failed ai_prompt_skill_id={AiPromptSkillId}", id);
            throw;
        }
        _loader.Invalidate();

        _logger.LogInformation("ai_prompt_skill.update.ok ai_prompt_skill_id={AiPromptSkillId}", row.AiPromptSkillId);
        return ToResponse(row, includeContent: true);
    }

    public async Task<ActionResult<AiPromptSkillResponse>> DeleteAsync(Guid id)
    {
        var row = await _skills.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_prompt_skill.delete.not_found ai_prompt_skill_id={AiPromptSkillId}", id);
            return new NotFoundObjectResult(new { error = "prompt skill not found" });
        }

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _skills.SaveChangesAsync();
        _loader.Invalidate();

        _logger.LogInformation("ai_prompt_skill.delete.ok ai_prompt_skill_id={AiPromptSkillId}", row.AiPromptSkillId);
        return ToResponse(row, includeContent: false);
    }

    // base.md always leads the concatenation. Everything else goes behind
    // it; picking 100 gives operators headroom on both sides if they
    // want to insert something before or after by hand via Update.
    private static int DefaultSortOrder(string name) =>
        name.Equals("base.md", StringComparison.OrdinalIgnoreCase) ? 0 : 100;

    private BadRequestObjectResult? ValidatePayload(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new BadRequestObjectResult(new { error = "name is required" });
        if (!NamePattern.IsMatch(name.Trim()))
            return new BadRequestObjectResult(new { error = "name must match ^[a-zA-Z0-9_\\-]+\\.md$" });
        if (content is null)
            return new BadRequestObjectResult(new { error = "content is required" });
        if (content.Length > MaxContentBytes)
            return new BadRequestObjectResult(new { error = $"content exceeds {MaxContentBytes} bytes" });
        return null;
    }

    private static AiPromptSkillResponse ToResponse(PromptSkillModel s, bool includeContent) => new()
    {
        AiPromptSkillId = s.AiPromptSkillId,
        Name = s.Name,
        Content = includeContent ? s.Content : string.Empty,
        SortOrder = s.SortOrder,
        SizeBytes = s.Content?.Length ?? 0,
        IsActive = s.IsActive,
        CreatedBy = s.CreatedBy,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
