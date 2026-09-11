using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// CRUD for prompt skills (tabla ai_prompt_skills). Does not extend
// IBaseService because the upsert flow (Create routes through the same
// service method that Update does when the name already exists) and the
// cache-invalidation hook do not fit the generic CRUD shape.
public interface IAiPromptSkill
{
    Task<ActionResult<ListResponse<AiPromptSkillResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<AiPromptSkillResponse>> GetByIdAsync(Guid id);

    Task<ActionResult<AiPromptSkillResponse>> PostAsync(CreateAiPromptSkill dto);

    Task<ActionResult<AiPromptSkillResponse>> UpdateAsync(Guid id, UpdateAiPromptSkill dto);

    Task<ActionResult<AiPromptSkillResponse>> DeleteAsync(Guid id);
}
