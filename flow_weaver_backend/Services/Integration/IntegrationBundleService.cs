using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Ai.Specs;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using AiPromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using AiApiSpecModel = flow_weaver_backend.Models.AiApiSpec;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Services.Integration;

public interface IIntegrationBundleService
{
    Task<CreateIntegrationBundleResult> CreateAsync(CreateIntegrationBundle dto, CancellationToken ct);
}

// Creates an Integration + its associated AiPromptSkill / AiApiSpec rows
// + materializes IntegrationAction rows from the spec — all in one DB
// transaction. Throws DomainException subclasses on validation /
// conflict; the controller stays a thin adapter.
//
// Re-upload semantics (kept from the original controller code):
//   - A row with the same Name (skill) or Api (spec) that is IsActive=false
//     (soft-deleted) gets REACTIVATED in place: content/order/integration_id
//     replaced, IsActive flipped back to true. The DB unique index spans
//     active+inactive rows so this is the only way to avoid colliding
//     with a tombstone.
//   - A row that is IsActive=true → ConflictException; the caller must
//     rename or delete it via the regular admin surfaces first.
public sealed class IntegrationBundleService(
    IIntegrationRepository integrations,
    IAiPromptSkillRepository skills,
    IAiApiSpecRepository specs,
    IRepository<IntegrationActionModel> actions,
    IUnitOfWork uow,
    ICurrentUser caller,
    IntegrationService integrationService,
    ISkillPromptLoader skillLoader,
    IApiSpecIndex specIndex) : IIntegrationBundleService
{
    private static readonly string[] HttpVerbs =
        { "get:", "post:", "put:", "delete:", "patch:", "head:", "options:" };

    public async Task<CreateIntegrationBundleResult> CreateAsync(
        CreateIntegrationBundle dto, CancellationToken ct)
    {
        var i = dto.Integration
            ?? throw new ValidationException("integration body is required", "integration_required");

        if (string.IsNullOrWhiteSpace(i.Name))
            throw new ValidationException("integration.name is required", "name_required");
        if (string.IsNullOrWhiteSpace(i.Type))
            throw new ValidationException("integration.type is required", "type_required");
        if (string.IsNullOrWhiteSpace(i.BaseURL))
            throw new ValidationException("integration.base_url is required", "base_url_required");

        // Normalize once so pre-validation and INSERTs agree on keys.
        // Skill names keep their casing (filename semantics); spec ids
        // follow AiApiSpecService and lowercase-trim.
        var normalizedSkills = dto.Skills
            .Select(s => new NormalizedSkill(
                Name: (s.Name ?? string.Empty).Trim(),
                Content: s.Content ?? string.Empty,
                SortOrder: s.SortOrder ?? 100))
            .ToList();

        var normalizedSpecs = dto.Specs
            .Select(s => new NormalizedSpec(
                Api: (s.Api ?? string.Empty).Trim().ToLowerInvariant(),
                Content: s.Content ?? string.Empty))
            .ToList();

        foreach (var s in normalizedSkills)
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Content))
                throw new ValidationException("every skill needs a name and content", "skill_incomplete");

        foreach (var s in normalizedSpecs)
            if (string.IsNullOrWhiteSpace(s.Api) || string.IsNullOrWhiteSpace(s.Content))
                throw new ValidationException("every spec needs an api identifier and content", "spec_incomplete");

        var skillNames = normalizedSkills.Select(s => s.Name).ToList();
        if (skillNames.Distinct().Count() != skillNames.Count)
            throw new ConflictException("duplicate skill names inside the bundle", "duplicate_skill_name");

        var specApis = normalizedSpecs.Select(s => s.Api).ToList();
        if (specApis.Distinct().Count() != specApis.Count)
            throw new ConflictException("duplicate spec api identifiers inside the bundle", "duplicate_spec_api");


        // Load existing rows (active or tombstoned) for both kinds.
        var existingSkills = await skills.GetByNamesAsync(skillNames, ct);
        var existingSpecs = await specs.GetByApisAsync(specApis, ct);

        // Active collisions are user errors — fail fast, no transaction.
        var activeSkillClash = existingSkills.Values.FirstOrDefault(s => s.IsActive);
        if (activeSkillClash is not null)
            throw new ConflictException(
                $"skill '{activeSkillClash.Name}' already exists",
                "skill_already_exists");

        var activeSpecClash = existingSpecs.Values.FirstOrDefault(s => s.IsActive);
        if (activeSpecClash is not null)
            throw new ConflictException(
                $"spec '{activeSpecClash.Api}' already exists",
                "spec_already_exists");

        // One snapshot of the taken set for this bundle; BuildEntity does not
        // query it, so a second integration created in the same transaction
        // would otherwise be allocated a slug that is already spoken for.
        var integration = integrationService.BuildEntity(i, await integrations.ListTakenSlugsAsync(ct));
        var now = DateTime.UtcNow;
        var actionsCreated = 0;

        await uow.ExecuteInTransactionAsync(async c =>
        {
            integrations.Add(integration);

            foreach (var skill in normalizedSkills)
                UpsertSkill(skill, existingSkills, integration.IntegrationId, now);

            foreach (var spec in normalizedSpecs)
            {
                UpsertSpec(spec, existingSpecs, integration.IntegrationId, now);
                actionsCreated += MaterializeActions(spec, integration.IntegrationId, now);
            }

            await uow.SaveChangesAsync(c);
        }, ct);

        // Invalidate caches *after* commit; the agent's next message
        // will pull fresh prompt + spec index.
        if (normalizedSkills.Count > 0)
            skillLoader.Invalidate();
        if (normalizedSpecs.Count > 0)
            await specIndex.ReloadAsync(ct);

        return new CreateIntegrationBundleResult
        {
            Integration = IntegrationService.ToResponse(integration),
            SkillsCreated = normalizedSkills.Count,
            SpecsCreated = normalizedSpecs.Count,
            ActionsCreated = actionsCreated,
        };
    }

    private void UpsertSkill(
        NormalizedSkill skill,
        Dictionary<string, AiPromptSkillModel> existing,
        Guid integrationId,
        DateTime now)
    {
        if (existing.TryGetValue(skill.Name, out var row))
        {
            // Reactivate in place — we already confirmed it's inactive.
            row.Content = skill.Content;
            row.SortOrder = skill.SortOrder;
            row.IntegrationId = integrationId;
            row.CreatedBy = caller.UserId;
            row.IsActive = true;
            row.UpdatedAt = now;
            return;
        }

        skills.Add(new AiPromptSkillModel
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = skill.Name,
            Content = skill.Content,
            SortOrder = skill.SortOrder,
            IntegrationId = integrationId,
            CreatedBy = caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void UpsertSpec(
        NormalizedSpec spec,
        Dictionary<string, AiApiSpecModel> existing,
        Guid integrationId,
        DateTime now)
    {
        var opCount = CountOperations(spec.Content);

        if (existing.TryGetValue(spec.Api, out var row))
        {
            row.Content = spec.Content;
            row.OperationCount = opCount;
            row.IntegrationId = integrationId;
            row.CreatedBy = caller.UserId;
            row.IsActive = true;
            row.UpdatedAt = now;
            return;
        }

        specs.Add(new AiApiSpecModel
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = spec.Api,
            Content = spec.Content,
            OperationCount = opCount,
            IntegrationId = integrationId,
            CreatedBy = caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // Materialize the spec's operations as IntegrationAction rows so
    // they show up in the palette + integration_action nodes. Without
    // this step the agent's discover_operations sees the ops (via
    // IApiSpecIndex) but list_actions returns 0, and workflows can't
    // call the integration.
    private int MaterializeActions(
        NormalizedSpec spec, Guid integrationId, DateTime now)
    {
        IReadOnlyList<ApiOperation> operations;
        try
        {
            // ToList() must happen INSIDE the try: ParseOperations is a
            // yield-return iterator, so the YAML load is deferred until the
            // sequence is enumerated. Leaving the enumeration to the foreach
            // below threw straight past this catch and rolled back the whole
            // bundle — the opposite of the intent stated here.
            operations = YamlSpecIndex.ParseOperations(spec.Api, spec.Content).ToList();
        }
        catch
        {
            // A malformed YAML still gets the spec row saved (user may
            // want to edit it in-place); we skip action generation for it.
            operations = Array.Empty<ApiOperation>();
        }

        var count = 0;
        foreach (var op in operations)
        {
            actions.Add(new IntegrationActionModel
            {
                IntegrationActionId = Guid.NewGuid(),
                IntegrationId = integrationId,
                Name = StripOperationPrefix(op.OperationId),
                Description = string.IsNullOrWhiteSpace(op.Summary) ? op.Description : op.Summary,
                Method = op.Method,
                Path = op.Path,
                Category = op.Tags.Count > 0 ? op.Tags[0] : string.Empty,
                Enabled = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            count++;
        }
        return count;
    }

    // OperationIds in our specs follow `<api>:<verb>_<noun>` (e.g.
    // `fw_email:send_email`). The palette cares only about the trailing
    // action name; the `fw_email:` prefix is implied by the parent
    // integration. Falls back to the full operationId when no colon is
    // present, so ad-hoc specs without the convention still work.
    private static string StripOperationPrefix(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return string.Empty;
        var colon = operationId.LastIndexOf(':');
        return colon < 0 ? operationId : operationId[(colon + 1)..];
    }

    // Cheap first-pass OpenAPI operation count for the list UI. The
    // proper parse runs through YamlSpecIndex/IApiSpecIndex later; we
    // don't want to pull YamlDotNet into a hot CRUD path, and the count
    // is only displayed, not load-bearing.
    private static int CountOperations(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return 0;
        var count = 0;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            foreach (var verb in HttpVerbs)
            {
                if (trimmed.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private sealed record NormalizedSkill(string Name, string Content, int SortOrder);
    private sealed record NormalizedSpec(string Api, string Content);
}
