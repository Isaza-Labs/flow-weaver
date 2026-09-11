using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Specs;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AiApiSpecModel = flow_weaver_backend.Models.AiApiSpec;
using AiPromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;

namespace flow_weaver_backend.Services.Integration;

public interface IIntegrationCatalogService
{
    Task<ActionResult<IntegrationBundleView>> GetBundleAsync(Guid integrationId, CancellationToken ct);
    Task<ActionResult<AttachSpecResult>> AttachSpecAsync(Guid integrationId, AttachSpecRequest dto, CancellationToken ct);
    Task<ActionResult<AiPromptSkillResponse>> AttachSkillAsync(Guid integrationId, AttachSkillRequest dto, CancellationToken ct);
}

// Post-creation management of an integration's scoped skills / specs, plus
// the piece the bundle-create flow had but the edit flow lacked: attaching a
// spec to a LIVE integration re-materializes its IntegrationActions. This is
// what makes "load a new spec → the Actions list updates" work from the
// /integrations view without re-creating the integration.
//
// Action materialization is UPSERT-by-name (non-destructive): each operation
// in the spec creates or refreshes an action with that name; actions the user
// added by hand (different names) and actions from a previously-loaded spec
// are left intact. Uploading a spec therefore only ever adds/refreshes.
public sealed class IntegrationCatalogService : IIntegrationCatalogService
{
    private const int MaxSpecBytes = 2 * 1024 * 1024;
    private const int MaxSkillBytes = 512 * 1024;

    private static readonly Regex ApiPattern = new(@"^[a-z0-9_\-]+$", RegexOptions.Compiled);
    private static readonly Regex SkillNamePattern = new(@"^[a-zA-Z0-9_\-]+\.md$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly ISkillPromptLoader _skillLoader;
    private readonly IApiSpecIndex _specIndex;
    private readonly ILogger<IntegrationCatalogService> _logger;

    public IntegrationCatalogService(
        AppDbContext db,
        ICurrentUser caller,
        ISkillPromptLoader skillLoader,
        IApiSpecIndex specIndex,
        ILogger<IntegrationCatalogService> logger)
    {
        _db = db;
        _caller = caller;
        _skillLoader = skillLoader;
        _specIndex = specIndex;
        _logger = logger;
    }

    public async Task<ActionResult<IntegrationBundleView>> GetBundleAsync(Guid integrationId, CancellationToken ct)
    {
        if (!await IntegrationExistsAsync(integrationId, ct))
            return new NotFoundObjectResult(new { error = "integration not found" });

        var skills = await _db.AiPromptSkills.AsNoTracking()
            .Where(s => s.IntegrationId == integrationId && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(ct);

        var specs = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IntegrationId == integrationId && s.IsActive)
            .OrderBy(s => s.Api)
            .ToListAsync(ct);

        return new IntegrationBundleView
        {
            Skills = skills.Select(SkillResp).ToList(),
            Specs = specs.Select(SpecResp).ToList(),
        };
    }

    public async Task<ActionResult<AttachSpecResult>> AttachSpecAsync(
        Guid integrationId, AttachSpecRequest dto, CancellationToken ct)
    {
        if (!await IntegrationExistsAsync(integrationId, ct))
            return new NotFoundObjectResult(new { error = "integration not found" });

        var api = (dto.Api ?? string.Empty).Trim().ToLowerInvariant();
        if (!ApiPattern.IsMatch(api))
            return new BadRequestObjectResult(new { error = "api must match ^[a-z0-9_\\-]+$" });
        if (string.IsNullOrWhiteSpace(dto.Content))
            return new BadRequestObjectResult(new { error = "content is required" });
        if (dto.Content.Length > MaxSpecBytes)
            return new BadRequestObjectResult(new { error = $"content exceeds {MaxSpecBytes} bytes" });

        // Lenient parse — a malformed YAML still saves the spec row (so the
        // user can fix it in place) but materializes no actions.
        List<ApiOperation> ops;
        var parsed = true;
        try
        {
            ops = YamlSpecIndex.ParseOperations(api, dto.Content).ToList();
        }
        catch
        {
            ops = new List<ApiOperation>();
            parsed = false;
        }

        var now = DateTime.UtcNow;

        // Upsert the spec row. The unique (Api) index spans
        // active + soft-deleted rows, so at most one match — reuse it and
        // (re)link it to this integration.
        var spec = await _db.AiApiSpecs
            .FirstOrDefaultAsync(s => s.Api == api, ct);
        if (spec is null)
        {
            spec = new AiApiSpecModel
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                CreatedBy = _caller.UserId,
                CreatedAt = now,
            };
            _db.AiApiSpecs.Add(spec);
        }
        spec.Content = dto.Content;
        spec.OperationCount = ops.Count;
        spec.IntegrationId = integrationId;
        spec.IsActive = true;
        spec.UpdatedAt = now;

        var upserted = await UpsertActions(integrationId, ops, now, ct);

        await _db.SaveChangesAsync(ct);
        await _specIndex.ReloadAsync(ct);

        _logger.LogInformation(
            "integration.catalog.attach_spec.ok integration_id={IntegrationId} api={Api} actions_upserted={Count} parsed={Parsed}",
            integrationId, api, upserted, parsed);

        return new AttachSpecResult
        {
            Spec = SpecResp(spec),
            ActionsUpserted = upserted,
            SpecUnparsed = !parsed,
        };
    }

    public async Task<ActionResult<AiPromptSkillResponse>> AttachSkillAsync(
        Guid integrationId, AttachSkillRequest dto, CancellationToken ct)
    {
        if (!await IntegrationExistsAsync(integrationId, ct))
            return new NotFoundObjectResult(new { error = "integration not found" });

        var name = (dto.Name ?? string.Empty).Trim();
        if (!SkillNamePattern.IsMatch(name))
            return new BadRequestObjectResult(new { error = "name must match ^[a-zA-Z0-9_\\-]+\\.md$" });
        if (string.IsNullOrWhiteSpace(dto.Content))
            return new BadRequestObjectResult(new { error = "content is required" });
        if (dto.Content.Length > MaxSkillBytes)
            return new BadRequestObjectResult(new { error = $"content exceeds {MaxSkillBytes} bytes" });

        var now = DateTime.UtcNow;

        var skill = await _db.AiPromptSkills
            .FirstOrDefaultAsync(s => s.Name == name, ct);
        if (skill is null)
        {
            skill = new AiPromptSkillModel
            {
                AiPromptSkillId = Guid.NewGuid(),
                Name = name,
                CreatedBy = _caller.UserId,
                CreatedAt = now,
            };
            _db.AiPromptSkills.Add(skill);
        }
        skill.Content = dto.Content;
        skill.SortOrder = dto.SortOrder
            ?? (name.Equals("base.md", StringComparison.OrdinalIgnoreCase) ? 0 : 100);
        skill.IntegrationId = integrationId;
        skill.IsActive = true;
        skill.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _skillLoader.Invalidate();

        _logger.LogInformation(
            "integration.catalog.attach_skill.ok integration_id={IntegrationId} name={Name}",
            integrationId, name);

        return SkillResp(skill);
    }

    // Upsert one action per operation, keyed on (integration, name). Refreshes
    // matching rows in place; adds new ones; never deletes — so manual actions
    // and prior-spec actions survive a re-upload.
    private async Task<int> UpsertActions(
        Guid integrationId, List<ApiOperation> ops, DateTime now, CancellationToken ct)
    {
        if (ops.Count == 0) return 0;

        var existing = await _db.IntegrationActions
            .Where(a => a.IntegrationId == integrationId && a.IsActive)
            .ToListAsync(ct);
        var byName = new Dictionary<string, IntegrationActionModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in existing) byName.TryAdd(a.Name, a);

        var count = 0;
        foreach (var op in ops)
        {
            var name = StripOperationPrefix(op.OperationId);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var description = string.IsNullOrWhiteSpace(op.Summary) ? op.Description : op.Summary;
            var category = op.Tags.Count > 0 ? op.Tags[0] : string.Empty;

            if (byName.TryGetValue(name, out var row))
            {
                row.Method = op.Method;
                row.Path = op.Path;
                row.Description = description;
                if (!string.IsNullOrEmpty(category)) row.Category = category;
                row.Enabled = true;
                row.IsActive = true;
                row.UpdatedAt = now;
            }
            else
            {
                _db.IntegrationActions.Add(new IntegrationActionModel
                {
                    IntegrationActionId = Guid.NewGuid(),
                    IntegrationId = integrationId,
                    Name = name,
                    Description = description,
                    Method = op.Method,
                    Path = op.Path,
                    Category = category,
                    Enabled = true,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            count++;
        }
        return count;
    }

    private async Task<bool> IntegrationExistsAsync(Guid integrationId, CancellationToken ct) =>
        await _db.Integrations.AsNoTracking().AnyAsync(
            i => i.IntegrationId == integrationId && i.IsActive, ct);

    // OperationIds follow `<api>:<verb>_<noun>`; the action name drops the
    // prefix (implied by the parent integration). Mirrors IntegrationBundleService.
    private static string StripOperationPrefix(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return string.Empty;
        var colon = operationId.LastIndexOf(':');
        return colon < 0 ? operationId : operationId[(colon + 1)..];
    }

    private static AiApiSpecResponse SpecResp(AiApiSpecModel s) => new()
    {
        AiApiSpecId = s.AiApiSpecId,
        Api = s.Api,
        Content = s.Content,
        OperationCount = s.OperationCount,
        SizeBytes = s.Content?.Length ?? 0,
        IsActive = s.IsActive,
        CreatedBy = s.CreatedBy,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };

    private static AiPromptSkillResponse SkillResp(AiPromptSkillModel s) => new()
    {
        AiPromptSkillId = s.AiPromptSkillId,
        Name = s.Name,
        Content = s.Content,
        SortOrder = s.SortOrder,
        SizeBytes = s.Content?.Length ?? 0,
        IsActive = s.IsActive,
        CreatedBy = s.CreatedBy,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
