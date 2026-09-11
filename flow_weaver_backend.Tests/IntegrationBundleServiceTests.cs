using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Permission;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// IntegrationBundleService creates an Integration plus its skills, specs and
// materialized actions in one shot — the "upload a bundle" admin path. The
// behaviour worth pinning is the re-upload contract: tombstoned rows are
// reactivated in place (the unique index spans soft-deleted rows, so an INSERT
// would collide), while a live row is a conflict the user must resolve.
public class IntegrationBundleServiceTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // Records cache invalidations so the "after commit" ordering is observable.
    private sealed class RecordingSkillLoader : ISkillPromptLoader
    {
        public int Invalidated { get; private set; }
        public Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public void Invalidate() => Invalidated++;
    }

    private sealed class RecordingSpecIndex : IApiSpecIndex
    {
        public int Reloaded { get; private set; }
        public Task ReloadAsync(CancellationToken ct = default)
        {
            Reloaded++;
            return Task.CompletedTask;
        }
        public IReadOnlyList<ApiOperation> All() => Array.Empty<ApiOperation>();
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null)
            => Array.Empty<ApiOperation>();
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private sealed class AllowAllPermissions : IResourcePermissionService
    {
        public Task<bool> HasAtLeastAsync(string resourceType, Guid resourceId, string requiredRole, CancellationToken ct)
            => Task.FromResult(true);
        public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(string resourceType, Guid resourceId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ResourcePermissionResponse> GrantAsync(string resourceType, Guid resourceId, GrantResourcePermissionRequest dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RevokeAsync(Guid permissionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingSkillLoader SkillLoader { get; } = new();
        public RecordingSpecIndex SpecIndex { get; } = new();

        public IntegrationBundleService Build()
        {
            var caller = new FakeUser();
            var integrationRepo = new IntegrationRepository(Db);
            var integrationService = new IntegrationService(
                integrationRepo, caller, new FakeAudit(), new FakeTrace(),
                new AllowAllPermissions(), new FakeAppSettings(),
                new FakeOAuthTokens(),
                NullLogger<IntegrationService>.Instance);

            return new IntegrationBundleService(
                integrationRepo,
                new AiPromptSkillRepository(Db),
                new AiApiSpecRepository(Db),
                new RepositoryBase<IntegrationAction>(Db),
                new UnitOfWork(Db),
                caller,
                integrationService,
                SkillLoader,
                SpecIndex);
        }

        public void Dispose() => Db.Dispose();
    }

    private const string TwoOpSpec = """
        paths:
          /devices:
            get:
              operationId: netbox:list_devices
              summary: List devices
              tags: [dcim]
            post:
              operationId: netbox:create_device
        """;

    private static CreateIntegrationBundle Bundle(
        string name = "netbox",
        string type = "rest",
        string baseUrl = "https://netbox.example.com",
        List<BundledSkill>? skills = null,
        List<BundledSpec>? specs = null)
        => new()
        {
            Integration = new CreateIntegration { Name = name, Type = type, BaseURL = baseUrl },
            Skills = skills ?? new(),
            Specs = specs ?? new(),
        };

    private static List<BundledSkill> OneSkill(string name = "netbox-usage", int? sortOrder = null)
        => new() { new BundledSkill { Name = name, Content = "Use NetBox for IPAM.", SortOrder = sortOrder } };

    private static List<BundledSpec> OneSpec(string api = "netbox", string content = TwoOpSpec)
        => new() { new BundledSpec { Api = api, Content = content } };

    // ─── validation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "rest", "https://x", "name_required")]
    [InlineData("  ", "rest", "https://x", "name_required")]
    [InlineData("netbox", "", "https://x", "type_required")]
    [InlineData("netbox", "rest", "", "base_url_required")]
    public async Task MissingIntegrationFields_AreRejected(
        string name, string type, string baseUrl, string expectedCode)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(Bundle(name, type, baseUrl), default));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Fact]
    public async Task NullIntegrationBody_IsRejected()
    {
        using var f = new Fixture();
        var dto = Bundle();
        dto.Integration = null!;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(dto, default));

        Assert.Equal("integration_required", ex.Code);
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData("name", "")]
    public async Task IncompleteSkill_IsRejected(string skillName, string content)
    {
        using var f = new Fixture();
        var dto = Bundle(skills: new() { new BundledSkill { Name = skillName, Content = content } });

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(dto, default));

        Assert.Equal("skill_incomplete", ex.Code);
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData("api", "")]
    public async Task IncompleteSpec_IsRejected(string api, string content)
    {
        using var f = new Fixture();
        var dto = Bundle(specs: new() { new BundledSpec { Api = api, Content = content } });

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => f.Build().CreateAsync(dto, default));

        Assert.Equal("spec_incomplete", ex.Code);
    }

    // Duplicates inside one payload are caught before touching the DB, where
    // they would otherwise surface as an opaque unique-index violation.
    [Fact]
    public async Task DuplicateSkillNamesInsideBundle_AreRejected()
    {
        using var f = new Fixture();
        var dto = Bundle(skills: new()
        {
            new BundledSkill { Name = "dup", Content = "a" },
            new BundledSkill { Name = "dup", Content = "b" },
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => f.Build().CreateAsync(dto, default));

        Assert.Equal("duplicate_skill_name", ex.Code);
    }

    // Spec api ids are lowercased before the duplicate check, so casing
    // variants must still collide.
    [Fact]
    public async Task DuplicateSpecApisInsideBundle_AreRejectedCaseInsensitively()
    {
        using var f = new Fixture();
        var dto = Bundle(specs: new()
        {
            new BundledSpec { Api = "NetBox", Content = TwoOpSpec },
            new BundledSpec { Api = " netbox ", Content = TwoOpSpec },
        });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => f.Build().CreateAsync(dto, default));

        Assert.Equal("duplicate_spec_api", ex.Code);
    }

    // ─── happy path ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreatesIntegrationSkillsSpecsAndActions()
    {
        using var f = new Fixture();

        var result = await f.Build().CreateAsync(
            Bundle(skills: OneSkill(), specs: OneSpec()), default);

        Assert.Equal(1, result.SkillsCreated);
        Assert.Equal(1, result.SpecsCreated);
        Assert.Equal(2, result.ActionsCreated);
        Assert.Equal("netbox", result.Integration.Name);

        Assert.Single(f.Db.Integrations);
        Assert.Single(f.Db.AiPromptSkills);
        Assert.Single(f.Db.AiApiSpecs);
        Assert.Equal(2, await f.Db.IntegrationActions.CountAsync());
    }

    // Every child row must point at the new integration — that FK is what makes
    // the palette show the actions under it.
    [Fact]
    public async Task ChildRowsAreLinkedToTheNewIntegration()
    {
        using var f = new Fixture();

        var result = await f.Build().CreateAsync(
            Bundle(skills: OneSkill(), specs: OneSpec()), default);

        var id = result.Integration.IntegrationId;
        Assert.Equal(id, (await f.Db.AiPromptSkills.SingleAsync()).IntegrationId);
        Assert.Equal(id, (await f.Db.AiApiSpecs.SingleAsync()).IntegrationId);
        Assert.All(await f.Db.IntegrationActions.ToListAsync(), a => Assert.Equal(id, a.IntegrationId));
    }

    [Fact]
    public async Task RowsCarryTheCreator()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(skills: OneSkill(), specs: OneSpec()), default);

        var skill = await f.Db.AiPromptSkills.SingleAsync();
        Assert.Equal(User, skill.CreatedBy);
        Assert.True(skill.IsActive);

        var spec = await f.Db.AiApiSpecs.SingleAsync();
        Assert.Equal(User, spec.CreatedBy);
    }

    [Fact]
    public async Task SpecApiIsNormalisedToLowercase()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(specs: OneSpec(api: "  NetBox  ")), default);

        Assert.Equal("netbox", (await f.Db.AiApiSpecs.SingleAsync()).Api);
    }

    // Skill names keep their casing — they carry filename semantics.
    [Fact]
    public async Task SkillNameKeepsCasingButIsTrimmed()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(skills: OneSkill(name: "  NetBox-Usage  ")), default);

        Assert.Equal("NetBox-Usage", (await f.Db.AiPromptSkills.SingleAsync()).Name);
    }

    [Fact]
    public async Task SkillSortOrderDefaultsTo100()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(skills: OneSkill()), default);

        Assert.Equal(100, (await f.Db.AiPromptSkills.SingleAsync()).SortOrder);
    }

    [Fact]
    public async Task SkillSortOrderIsHonouredWhenSupplied()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(skills: OneSkill(sortOrder: 5)), default);

        Assert.Equal(5, (await f.Db.AiPromptSkills.SingleAsync()).SortOrder);
    }

    // ─── action materialization ─────────────────────────────────────────

    // Without these rows the agent's discover_operations sees the ops but
    // list_actions returns 0 and no workflow can call the integration.
    [Fact]
    public async Task ActionsCarryMethodPathCategoryAndDescription()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(specs: OneSpec()), default);

        var actions = await f.Db.IntegrationActions.ToListAsync();
        var get = actions.Single(a => a.Method == "GET");
        Assert.Equal("/devices", get.Path);
        Assert.Equal("List devices", get.Description);
        Assert.Equal("dcim", get.Category);
        Assert.True(get.Enabled);
        Assert.True(get.IsActive);
    }

    // `netbox:list_devices` → `list_devices`: the prefix is implied by the
    // parent integration, so the palette shows the bare action name.
    [Fact]
    public async Task ActionNameStripsTheOperationIdPrefix()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(specs: OneSpec()), default);

        var names = (await f.Db.IntegrationActions.ToListAsync())
            .Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "create_device", "list_devices" }, names);
    }

    // Ad-hoc specs that don't follow the `<api>:<verb>_<noun>` convention keep
    // the whole operationId.
    [Fact]
    public async Task ActionNameFallsBackToFullOperationIdWithoutColon()
    {
        using var f = new Fixture();
        var spec = OneSpec(content: """
            paths:
              /ping:
                get:
                  operationId: ping_it
            """);

        await f.Build().CreateAsync(Bundle(specs: spec), default);

        Assert.Equal("ping_it", (await f.Db.IntegrationActions.SingleAsync()).Name);
    }

    [Fact]
    public async Task ActionDescriptionFallsBackToDescriptionWhenSummaryIsEmpty()
    {
        using var f = new Fixture();
        var spec = OneSpec(content: """
            paths:
              /ping:
                get:
                  operationId: ping
                  description: Long form text
            """);

        await f.Build().CreateAsync(Bundle(specs: spec), default);

        Assert.Equal("Long form text", (await f.Db.IntegrationActions.SingleAsync()).Description);
    }

    // A YAML we can't parse still persists the spec row so the admin can fix it
    // in place; only action generation is skipped.
    [Fact]
    public async Task MalformedSpecStillPersistsButYieldsNoActions()
    {
        using var f = new Fixture();

        var result = await f.Build().CreateAsync(
            Bundle(specs: OneSpec(content: "paths:\n  - [unclosed\n bad: : :")), default);

        Assert.Equal(1, result.SpecsCreated);
        Assert.Equal(0, result.ActionsCreated);
        Assert.Single(f.Db.AiApiSpecs);
        Assert.Empty(f.Db.IntegrationActions);
    }

    // OperationCount is a cheap line-scan used only for display; it counts verb
    // lines, so it can legitimately differ from the parsed operation count.
    [Fact]
    public async Task SpecOperationCountIsPopulated()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(specs: OneSpec()), default);

        Assert.Equal(2, (await f.Db.AiApiSpecs.SingleAsync()).OperationCount);
    }

    // ─── re-upload semantics ────────────────────────────────────────────

    private async Task SeedTombstonedSkill(Fixture f, string name, Guid? oldIntegration = null)
    {
        f.Db.AiPromptSkills.Add(new AiPromptSkill
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = name,
            Content = "old content",
            SortOrder = 999,
            IntegrationId = oldIntegration,
            IsActive = false,
        });
        await f.Db.SaveChangesAsync();
    }

    // The unique index spans active + inactive rows, so a re-upload after a
    // delete has to reactivate in place rather than INSERT.
    [Fact]
    public async Task TombstonedSkillIsReactivatedInPlace()
    {
        using var f = new Fixture();
        await SeedTombstonedSkill(f, "netbox-usage");

        var result = await f.Build().CreateAsync(Bundle(skills: OneSkill()), default);

        var skill = Assert.Single(f.Db.AiPromptSkills);   // no second row
        Assert.True(skill.IsActive);
        Assert.Equal("Use NetBox for IPAM.", skill.Content);
        Assert.Equal(100, skill.SortOrder);
        Assert.Equal(result.Integration.IntegrationId, skill.IntegrationId);
        Assert.Equal(User, skill.CreatedBy);
    }

    [Fact]
    public async Task TombstonedSpecIsReactivatedInPlace()
    {
        using var f = new Fixture();
        f.Db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = "old: yaml",
            OperationCount = 0,
            IsActive = false,
        });
        await f.Db.SaveChangesAsync();

        var result = await f.Build().CreateAsync(Bundle(specs: OneSpec()), default);

        var spec = Assert.Single(f.Db.AiApiSpecs);
        Assert.True(spec.IsActive);
        Assert.Equal(TwoOpSpec, spec.Content);
        Assert.Equal(2, spec.OperationCount);
        Assert.Equal(result.Integration.IntegrationId, spec.IntegrationId);
        // Reactivation still materializes the actions for the new integration.
        Assert.Equal(2, result.ActionsCreated);
    }

    // A live row is a user error: renaming or deleting it is an explicit
    // decision, not something the bundle upload should do silently.
    [Fact]
    public async Task ActiveSkillClash_IsAConflict()
    {
        using var f = new Fixture();
        f.Db.AiPromptSkills.Add(new AiPromptSkill
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = "netbox-usage",
            Content = "live",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => f.Build().CreateAsync(Bundle(skills: OneSkill()), default));

        Assert.Equal("skill_already_exists", ex.Code);
        Assert.Contains("netbox-usage", ex.Message);
    }

    [Fact]
    public async Task ActiveSpecClash_IsAConflict()
    {
        using var f = new Fixture();
        f.Db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = "live: yaml",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => f.Build().CreateAsync(Bundle(specs: OneSpec()), default));

        Assert.Equal("spec_already_exists", ex.Code);
    }

    // A clash fails before the transaction opens, so nothing is written.
    [Fact]
    public async Task ConflictLeavesNoIntegrationBehind()
    {
        using var f = new Fixture();
        f.Db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = "live: yaml",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(
            () => f.Build().CreateAsync(Bundle(specs: OneSpec()), default));

        Assert.Empty(f.Db.Integrations);
        Assert.Empty(f.Db.IntegrationActions);
    }

    // ─── cache invalidation ─────────────────────────────────────────────

    [Fact]
    public async Task CachesAreInvalidated()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(skills: OneSkill(), specs: OneSpec()), default);

        Assert.Equal(1, f.SkillLoader.Invalidated);
        Assert.Equal(1, f.SpecIndex.Reloaded);
    }

    // Nothing to invalidate when the bundle carried none of that kind — the
    // spec index reload in particular is not free.
    [Fact]
    public async Task CachesAreNotTouchedWhenNothingOfThatKindWasUploaded()
    {
        using var f = new Fixture();

        await f.Build().CreateAsync(Bundle(), default);

        Assert.Equal(0, f.SkillLoader.Invalidated);
        Assert.Equal(0, f.SpecIndex.Reloaded);
    }

    [Fact]
    public async Task IntegrationOnlyBundleIsValid()
    {
        using var f = new Fixture();

        var result = await f.Build().CreateAsync(Bundle(), default);

        Assert.Equal(0, result.SkillsCreated);
        Assert.Equal(0, result.SpecsCreated);
        Assert.Equal(0, result.ActionsCreated);
        Assert.Single(f.Db.Integrations);
    }
}
