using flow_weaver_backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Interfaces;

// CRUD for API specs (tabla ai_api_specs). Same shape as IAiPromptSkill
// — kept separate so the two domains can diverge (spec service validates
// YAML + counts operations on every write).
public interface IAiApiSpec
{
    Task<ActionResult<ListResponse<AiApiSpecResponse>>> GetAsync(int limit = 50, int offset = 0);

    Task<ActionResult<AiApiSpecResponse>> GetByIdAsync(Guid id);

    Task<ActionResult<AiApiSpecResponse>> PostAsync(CreateAiApiSpec dto);

    Task<ActionResult<AiApiSpecResponse>> UpdateAsync(Guid id, UpdateAiApiSpec dto);

    Task<ActionResult<AiApiSpecResponse>> DeleteAsync(Guid id);
}
