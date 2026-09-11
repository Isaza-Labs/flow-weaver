using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Public read surface for the agent catalog (system prompt + parsed API
// operations) that every authenticated user can consume. Both the prompt
// and the operations returned come from the ai_prompt_skills /
// ai_api_specs rows. Admins write through the separate
// /api/admin/prompt-skills and /api/admin/api-specs controllers.
[ApiController]
[Route("api/ai/catalog")]
[HasPermission("aicatalog.read")]
public class AiCatalogController : ControllerBase
{
    private readonly ISkillPromptLoader _skills;
    private readonly IApiSpecIndex _specs;
    private readonly ICurrentUser _caller;

    public AiCatalogController(ISkillPromptLoader skills, IApiSpecIndex specs, ICurrentUser caller)
    {
        _skills = skills;
        _specs = specs;
        _caller = caller;
    }

    [HttpGet("prompt")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<object>> GetSystemPrompt(CancellationToken ct)
    {
        var ctx = new SkillTemplateContext
        {
            CurrentDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ToolList = string.Empty,
        };
        var prompt = await _skills.LoadAsync(ctx, ct);
        return new OkObjectResult(new { prompt });
    }

    [HttpGet("operations")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public ActionResult<IEnumerable<ApiOperation>> ListOperations(
        [FromQuery] string? keyword,
        [FromQuery] string? api,
        [FromQuery] string? method)
    {
        if (string.IsNullOrWhiteSpace(keyword) && string.IsNullOrWhiteSpace(api) && string.IsNullOrWhiteSpace(method))
            return new OkObjectResult(_specs.All());

        return new OkObjectResult(_specs.Search(keyword ?? string.Empty, api, method));
    }

    [HttpGet("operations/{operationId}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public ActionResult<ApiOperation> GetOperation(string operationId)
    {
        var op = _specs.GetByOperationId(operationId);
        return op is null
            ? Problems.NotFound("operation", operationId)
            : op;
    }

    [HttpPost("reload")]
    [HasPermission("aicatalog.reload")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<object>> Reload(CancellationToken ct)
    {
        _skills.Invalidate();
        await _specs.ReloadAsync(ct);
        return new OkObjectResult(new
        {
            reloaded = true,
            operation_count = _specs.All().Count,
        });
    }
}
