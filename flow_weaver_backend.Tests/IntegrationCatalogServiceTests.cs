using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

// Post-creation catalog management: attaching a spec/skill to a LIVE
// integration. The property that matters most is that action materialization
// is a non-destructive UPSERT — uploading a new spec must refresh and add, but
// never delete hand-made actions or ones from a previously-loaded spec.
public class IntegrationCatalogServiceTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

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

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingSkillLoader SkillLoader { get; } = new();
        public RecordingSpecIndex SpecIndex { get; } = new();

        public IntegrationCatalogService Build() => new(
            Db, new FakeUser(), SkillLoader, SpecIndex,
            NullLogger<IntegrationCatalogService>.Instance);

        public Guid SeedIntegration(bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "netbox",
                BaseURL = "https://netbox.example.com",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
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
              summary: Create a device
        """;

    // Unwraps the ActionResult<T> value, failing the test if the service
    // returned an error result instead.
    private static T Value<T>(ActionResult<T> result) where T : class
    {
        Assert.Null(result.Result);
        return Assert.IsType<T>(result.Value);
    }

    private static AttachSpecRequest SpecReq(string api = "netbox", string content = TwoOpSpec)
        => new() { Api = api, Content = content };

    private static AttachSkillRequest SkillReq(
        string name = "netbox.md", string content = "Use NetBox.", int? sortOrder = null)
        => new() { Name = name, Content = content, SortOrder = sortOrder };

    // ─── integration existence ──────────────────────────────────────────

    [Fact]
    public async Task GetBundle_UnknownIntegration_IsNotFound()
    {
        using var f = new Fixture();

        var result = await f.Build().GetBundleAsync(Guid.NewGuid(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachSpec_UnknownIntegration_IsNotFound()
    {
        using var f = new Fixture();

        var result = await f.Build().AttachSpecAsync(Guid.NewGuid(), SpecReq(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(f.Db.AiApiSpecs);
    }

    [Fact]
    public async Task AttachSkill_UnknownIntegration_IsNotFound()
    {
        using var f = new Fixture();

        var result = await f.Build().AttachSkillAsync(Guid.NewGuid(), SkillReq(), default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(f.Db.AiPromptSkills);
    }

    [Fact]
    public async Task SoftDeletedIntegration_IsNotFound()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration(active: false);

        Assert.IsType<NotFoundObjectResult>((await f.Build().GetBundleAsync(id, default)).Result);
    }

    // ─── AttachSpec validation ──────────────────────────────────────────

    [Theory]
    [InlineData("Net Box")]      // space
    [InlineData("net.box")]      // dot
    [InlineData("net/box")]      // slash
    [InlineData("")]             // empty
    public async Task AttachSpec_InvalidApiIdentifier_IsBadRequest(string api)
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSpecAsync(id, SpecReq(api: api), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // Casing and surrounding whitespace are normalised, not rejected.
    [Fact]
    public async Task AttachSpec_ApiIsTrimmedAndLowercased()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSpecAsync(id, SpecReq(api: "  NetBox  "), default);

        Assert.Equal("netbox", Value(result).Spec.Api);
    }

    [Fact]
    public async Task AttachSpec_EmptyContent_IsBadRequest()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSpecAsync(id, SpecReq(content: "   "), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachSpec_OversizedContent_IsBadRequest()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var huge = new string('x', 2 * 1024 * 1024 + 1);

        var result = await f.Build().AttachSpecAsync(id, SpecReq(content: huge), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── AttachSpec behaviour ───────────────────────────────────────────

    [Fact]
    public async Task AttachSpec_CreatesSpecAndMaterializesActions()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSpecAsync(id, SpecReq(), default));

        Assert.Equal(2, result.ActionsUpserted);
        Assert.False(result.SpecUnparsed);
        Assert.Equal(2, result.Spec.OperationCount);

        var spec = await f.Db.AiApiSpecs.SingleAsync();
        Assert.Equal(id, spec.IntegrationId);
        Assert.Equal(User, spec.CreatedBy);
        Assert.True(spec.IsActive);
        Assert.Equal(2, await f.Db.IntegrationActions.CountAsync());
    }

    [Fact]
    public async Task AttachSpec_ActionsCarrySpecMetadata()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        var get = await f.Db.IntegrationActions.SingleAsync(a => a.Method == "GET");
        Assert.Equal("list_devices", get.Name);   // `netbox:` prefix stripped
        Assert.Equal("/devices", get.Path);
        Assert.Equal("List devices", get.Description);
        Assert.Equal("dcim", get.Category);
        Assert.True(get.Enabled);
    }

    [Fact]
    public async Task AttachSpec_ReloadsTheSpecIndex()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        Assert.Equal(1, f.SpecIndex.Reloaded);
    }

    // A malformed spec still persists so the user can fix it in place; the
    // result flags it so the UI can warn instead of silently showing 0 actions.
    [Fact]
    public async Task AttachSpec_MalformedYaml_SavesSpecFlaggedUnparsed()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSpecAsync(
            id, SpecReq(content: "paths:\n  - [unclosed\n bad: : :"), default));

        Assert.True(result.SpecUnparsed);
        Assert.Equal(0, result.ActionsUpserted);
        Assert.Single(f.Db.AiApiSpecs);
        Assert.Empty(f.Db.IntegrationActions);
    }

    // Re-attaching the same api reuses the row (the unique index spans
    // soft-deleted rows, so a second INSERT would collide).
    [Fact]
    public async Task AttachSpec_SameApiTwice_UpdatesTheSameRow()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var svc = f.Build();
        await svc.AttachSpecAsync(id, SpecReq(), default);

        var second = Value(await svc.AttachSpecAsync(id, SpecReq(content: """
            paths:
              /ping:
                get:
                  operationId: netbox:ping
            """), default));

        var spec = Assert.Single(f.Db.AiApiSpecs);
        Assert.Contains("/ping", spec.Content);
        Assert.Equal(1, spec.OperationCount);
        Assert.Equal(1, second.ActionsUpserted);
    }

    // A previously soft-deleted spec is revived and relinked rather than
    // duplicated.
    [Fact]
    public async Task AttachSpec_TombstonedRow_IsReactivatedAndRelinked()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        f.Db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = "netbox",
            Content = "old: yaml",
            IsActive = false,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        var spec = Assert.Single(f.Db.AiApiSpecs);
        Assert.True(spec.IsActive);
        Assert.Equal(id, spec.IntegrationId);
        Assert.Equal(TwoOpSpec, spec.Content);
    }

    // ─── non-destructive action upsert ──────────────────────────────────

    // The whole point of the upsert: a hand-made action keeps existing after a
    // spec re-upload that doesn't mention it.
    [Fact]
    public async Task AttachSpec_LeavesUnrelatedActionsIntact()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        f.Db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = id,
            Name = "hand_made",
            Method = "GET",
            Path = "/custom",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        Assert.Equal(3, await f.Db.IntegrationActions.CountAsync());
        Assert.True(await f.Db.IntegrationActions.AnyAsync(a => a.Name == "hand_made"));
    }

    // A name collision refreshes the existing row in place instead of adding a
    // duplicate.
    [Fact]
    public async Task AttachSpec_RefreshesMatchingActionInPlace()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var actionId = Guid.NewGuid();
        f.Db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = actionId,
            IntegrationId = id,
            Name = "list_devices",
            Method = "POST",           // stale
            Path = "/old",             // stale
            Description = "outdated",
            Enabled = false,
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        Assert.Equal(2, await f.Db.IntegrationActions.CountAsync());  // not 3
        var row = await f.Db.IntegrationActions.SingleAsync(a => a.IntegrationActionId == actionId);
        Assert.Equal("GET", row.Method);
        Assert.Equal("/devices", row.Path);
        Assert.Equal("List devices", row.Description);
        Assert.True(row.Enabled);
    }

    // Action names are matched case-insensitively, so casing drift in a spec
    // doesn't fork the row.
    [Fact]
    public async Task AttachSpec_ActionMatchIsCaseInsensitive()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        f.Db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = id,
            Name = "LIST_DEVICES",
            Method = "POST",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        Assert.Equal(2, await f.Db.IntegrationActions.CountAsync());
    }

    // An empty category must not blank out one a user set by hand.
    [Fact]
    public async Task AttachSpec_EmptyCategoryDoesNotOverwriteExistingOne()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        f.Db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = id,
            Name = "create_device",   // the spec's POST op has no tags
            Category = "manual-category",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        var row = await f.Db.IntegrationActions.SingleAsync(a => a.Name == "create_device");
        Assert.Equal("manual-category", row.Category);
    }

    // Actions belonging to a different integration must not be touched.
    [Fact]
    public async Task AttachSpec_DoesNotTouchOtherIntegrationsActions()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var otherId = f.SeedIntegration();
        f.Db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = otherId,
            Name = "list_devices",
            Method = "POST",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSpecAsync(id, SpecReq(), default);

        Assert.Equal(3, await f.Db.IntegrationActions.CountAsync());
        var untouched = await f.Db.IntegrationActions
            .SingleAsync(a => a.IntegrationId == otherId);
        Assert.Equal("POST", untouched.Method);
    }

    // ─── AttachSkill ────────────────────────────────────────────────────

    [Theory]
    [InlineData("netbox")]        // no .md
    [InlineData("net box.md")]    // space
    [InlineData("net.box.md")]    // dot in stem
    [InlineData("")]
    public async Task AttachSkill_InvalidName_IsBadRequest(string name)
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSkillAsync(id, SkillReq(name: name), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachSkill_EmptyContent_IsBadRequest()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSkillAsync(id, SkillReq(content: "  "), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachSkill_OversizedContent_IsBadRequest()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = await f.Build().AttachSkillAsync(
            id, SkillReq(content: new string('x', 512 * 1024 + 1)), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AttachSkill_CreatesRowAndInvalidatesPromptCache()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSkillAsync(id, SkillReq(), default));

        Assert.Equal("netbox.md", result.Name);
        Assert.Equal(id, result.IntegrationId);
        Assert.Equal(1, f.SkillLoader.Invalidated);

        var skill = await f.Db.AiPromptSkills.SingleAsync();
        Assert.Equal(User, skill.CreatedBy);
        Assert.True(skill.IsActive);
    }

    // `base.md` is the system prompt's root document, so it must sort first.
    [Fact]
    public async Task AttachSkill_BaseMdDefaultsToSortOrderZero()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSkillAsync(id, SkillReq(name: "base.md"), default));

        Assert.Equal(0, result.SortOrder);
    }

    [Fact]
    public async Task AttachSkill_OtherNamesDefaultToSortOrder100()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSkillAsync(id, SkillReq(), default));

        Assert.Equal(100, result.SortOrder);
    }

    [Fact]
    public async Task AttachSkill_ExplicitSortOrderWinsOverDefault()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var result = Value(await f.Build().AttachSkillAsync(
            id, SkillReq(name: "base.md", sortOrder: 42), default));

        Assert.Equal(42, result.SortOrder);
    }

    [Fact]
    public async Task AttachSkill_SameNameTwice_UpdatesInPlace()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var svc = f.Build();
        await svc.AttachSkillAsync(id, SkillReq(), default);

        await svc.AttachSkillAsync(id, SkillReq(content: "Updated guidance."), default);

        var skill = Assert.Single(f.Db.AiPromptSkills);
        Assert.Equal("Updated guidance.", skill.Content);
    }

    [Fact]
    public async Task AttachSkill_TombstonedRow_IsReactivated()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        f.Db.AiPromptSkills.Add(new AiPromptSkill
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = "netbox.md",
            Content = "old",
            IsActive = false,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().AttachSkillAsync(id, SkillReq(), default);

        var skill = Assert.Single(f.Db.AiPromptSkills);
        Assert.True(skill.IsActive);
        Assert.Equal(id, skill.IntegrationId);
    }

    // ─── GetBundle ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetBundle_ReturnsOnlyThisIntegrationsActiveRows()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var otherId = f.SeedIntegration();
        var svc = f.Build();
        await svc.AttachSkillAsync(id, SkillReq(), default);
        await svc.AttachSkillAsync(otherId, SkillReq(name: "other.md"), default);
        await svc.AttachSpecAsync(id, SpecReq(), default);

        var view = Value(await svc.GetBundleAsync(id, default));

        Assert.Equal("netbox.md", Assert.Single(view.Skills).Name);
        Assert.Equal("netbox", Assert.Single(view.Specs).Api);
    }

    [Fact]
    public async Task GetBundle_ExcludesSoftDeletedRows()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        await f.Build().AttachSkillAsync(id, SkillReq(), default);
        (await f.Db.AiPromptSkills.SingleAsync()).IsActive = false;
        await f.Db.SaveChangesAsync();

        var view = Value(await f.Build().GetBundleAsync(id, default));

        Assert.Empty(view.Skills);
    }

    // Skills are ordered by SortOrder then name, so `base.md` leads the prompt.
    [Fact]
    public async Task GetBundle_OrdersSkillsBySortOrderThenName()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        var svc = f.Build();
        await svc.AttachSkillAsync(id, SkillReq(name: "zeta.md"), default);
        await svc.AttachSkillAsync(id, SkillReq(name: "alpha.md"), default);
        await svc.AttachSkillAsync(id, SkillReq(name: "base.md"), default);

        var view = Value(await svc.GetBundleAsync(id, default));

        Assert.Equal(new[] { "base.md", "alpha.md", "zeta.md" }, view.Skills.Select(s => s.Name));
    }

    [Fact]
    public async Task GetBundle_ReportsContentSizeBytes()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();
        await f.Build().AttachSkillAsync(id, SkillReq(content: "12345"), default);

        var view = Value(await f.Build().GetBundleAsync(id, default));

        Assert.Equal(5, Assert.Single(view.Skills).SizeBytes);
    }

    [Fact]
    public async Task GetBundle_EmptyIntegrationReturnsEmptyLists()
    {
        using var f = new Fixture();
        var id = f.SeedIntegration();

        var view = Value(await f.Build().GetBundleAsync(id, default));

        Assert.Empty(view.Skills);
        Assert.Empty(view.Specs);
    }
}
