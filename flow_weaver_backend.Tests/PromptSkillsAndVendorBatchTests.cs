using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Skills;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using flow_weaver_backend.Services.VendorCommand;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Tests;

// The system-prompt assembly, the command allowlist, and the agent's
// plan-feature listing.
//
// The prompt loader is cached, so the invariant that matters is freshness:
// an edit has to take effect without a restart.
public class PromptSkillsAndVendorBatchTests
{

    // ─── SkillPromptLoader ──────────────────────────────────────────────

    private sealed class LoaderFixture : IDisposable
    {
        private readonly string _dbName = Guid.NewGuid().ToString();
        public ServiceProvider Sp { get; }

        public LoaderFixture()
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddScoped<IAiPromptSkillRepository, AiPromptSkillRepository>();
            Sp = services.BuildServiceProvider();
        }

        public SkillPromptLoader Build()
            => new(Sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SkillPromptLoader>.Instance);

        public AppDbContext NewDb() => Sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public Guid Seed(
            string name, string content, int sortOrder = 100,
            bool active = true, DateTime? updatedAt = null)
        {
            using var db = NewDb();
            var id = Guid.NewGuid();
            db.AiPromptSkills.Add(new PromptSkillModel
            {
                AiPromptSkillId = id,
                Name = name,
                Content = content,
                SortOrder = sortOrder,
                IsActive = active,
                UpdatedAt = updatedAt ?? DateTime.UtcNow,
            });
            db.SaveChanges();
            return id;
        }

        public void Dispose() => Sp.Dispose();
    }

    private static SkillTemplateContext Ctx(
        string date = "2026-07-28", string tools = "list_workflows")
        => new() { CurrentDate = date, ToolList = tools };

    [Fact]
    public async Task Prompt_SkillsAreConcatenatedInSortOrder()
    {
        using var f = new LoaderFixture();
        f.Seed("ssh.md", "SSH GUIDANCE", sortOrder: 100);
        f.Seed("base.md", "BASE RULES", sortOrder: 0);

        var prompt = await f.Build().LoadAsync(Ctx());

        Assert.True(
            prompt.IndexOf("BASE RULES", StringComparison.Ordinal)
            < prompt.IndexOf("SSH GUIDANCE", StringComparison.Ordinal), prompt);
    }

    [Fact]
    public async Task Prompt_SectionsAreSeparated()
    {
        using var f = new LoaderFixture();
        f.Seed("base.md", "A", sortOrder: 0);
        f.Seed("ssh.md", "B", sortOrder: 1);

        Assert.Contains("---", await f.Build().LoadAsync(Ctx()));
    }

    // The template slots are what let one stored prompt serve every request
    // — a stale date or tool list would make the agent reason from fiction.
    [Fact]
    public async Task Prompt_TheTemplateSlotsAreSubstituted()
    {
        using var f = new LoaderFixture();
        f.Seed("base.md", "Today is {{CurrentDate}}. Tools: {{ToolList}}.");

        var prompt = await f.Build().LoadAsync(Ctx(date: "2026-07-28", tools: "run_workflow"));

        Assert.Contains("Today is 2026-07-28", prompt);
        Assert.Contains("Tools: run_workflow", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public async Task Prompt_NoSkillsGivesAnEmptyPrompt()
    {
        using var f = new LoaderFixture();

        Assert.Equal(string.Empty, await f.Build().LoadAsync(Ctx()));
    }

    [Fact]
    public async Task Prompt_SoftDeletedSkillsAreExcluded()
    {
        using var f = new LoaderFixture();
        f.Seed("base.md", "LIVE");
        f.Seed("gone.md", "DELETED", active: false);

        var prompt = await f.Build().LoadAsync(Ctx());

        Assert.Contains("LIVE", prompt);
        Assert.DoesNotContain("DELETED", prompt);
    }

    // A newer UpdatedAt invalidates the cache on its own, so an edit takes
    // effect without a restart.
    [Fact]
    public async Task Prompt_AnEditIsPickedUpWithoutAnExplicitInvalidate()
    {
        using var f = new LoaderFixture();
        var id = f.Seed("base.md", "ORIGINAL", updatedAt: DateTime.UtcNow.AddMinutes(-5));
        var loader = f.Build();
        Assert.Contains("ORIGINAL", await loader.LoadAsync(Ctx()));

        using (var db = f.NewDb())
        {
            var row = db.AiPromptSkills.Single(s => s.AiPromptSkillId == id);
            row.Content = "EDITED";
            row.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Contains("EDITED", await loader.LoadAsync(Ctx()));
    }

    [Fact]
    public async Task Prompt_InvalidateForcesAReload()
    {
        using var f = new LoaderFixture();
        var id = f.Seed("base.md", "ORIGINAL");
        var loader = f.Build();
        await loader.LoadAsync(Ctx());

        using (var db = f.NewDb())
        {
            db.AiPromptSkills.Single(s => s.AiPromptSkillId == id).Content = "EDITED";
            db.SaveChanges();
        }
        loader.Invalidate();

        Assert.Contains("EDITED", await loader.LoadAsync(Ctx()));
    }

    // ─── ListPlanFeaturesHandler ────────────────────────────────────────

    private sealed class PlanFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();

        public ListPlanFeaturesHandler Build() => new(
            new PlanFeatureRepository(Db),
            new FakeUser(),
            NullLogger<ListPlanFeaturesHandler>.Instance);

        public void Seed(
            Guid? planId = null, string? importToken = null, string status = "pending",
            int ordinal = 1, string title = "collect the LLDP table",
            bool active = true)
        {
            Db.PlanFeatures.Add(new PlanFeature
            {
                PlanFeatureId = Guid.NewGuid(),
                WorkflowPlanId = planId,
                ImportToken = importToken,
                Ordinal = ordinal,
                Title = title,
                Status = status,
                SnippetType = "ssh",
                IsActive = active,
            });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private static string? Error(JsonElement result)
        => result.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"plan_id":"not-a-guid"}""")]
    [InlineData("""{"plan_id":123}""")]
    [InlineData("""{"import_token":"   "}""")]
    public async Task PlanFeatures_NeitherSelectorMeansAnErrorPayload(string args)
    {
        using var f = new PlanFixture();

        var result = await f.Build().ExecuteAsync(TestJson.Element(args), default);

        Assert.Contains("either plan_id", Error(result));
    }

    [Fact]
    public async Task PlanFeatures_ListsAPlansFeaturesWithAStatusSummary()
    {
        using var f = new PlanFixture();
        var planId = Guid.NewGuid();
        f.Seed(planId: planId, status: "verified", ordinal: 1);
        f.Seed(planId: planId, status: "verified", ordinal: 2);
        f.Seed(planId: planId, status: "pending", ordinal: 3);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"plan_id\":\"" + planId + "\"}"), default);

        Assert.Null(Error(result));
        Assert.Equal(3, result.GetProperty("features").GetArrayLength());
        var summary = result.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("verified").GetInt32());
        Assert.Equal(1, summary.GetProperty("pending").GetInt32());
    }

    // The import wizard tracks its features by token instead of plan id.
    [Fact]
    public async Task PlanFeatures_CanBeSelectedByImportTokenInstead()
    {
        using var f = new PlanFixture();
        f.Seed(importToken: "tok-1");
        f.Seed(importToken: "tok-2");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("""{"import_token":"tok-1"}"""), default);

        Assert.Equal(1, result.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task PlanFeatures_SoftDeletedFeaturesAreExcluded()
    {
        using var f = new PlanFixture();
        var planId = Guid.NewGuid();
        f.Seed(planId: planId, active: false);

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"plan_id\":\"" + planId + "\"}"), default);

        Assert.Equal(0, result.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task PlanFeatures_TheEntryCarriesWhatTheAgentNeedsToReason()
    {
        using var f = new PlanFixture();
        var planId = Guid.NewGuid();
        f.Seed(planId: planId, ordinal: 7, title: "collect the LLDP table");

        var result = await f.Build().ExecuteAsync(
            TestJson.Element("{\"plan_id\":\"" + planId + "\"}"), default);

        var feature = result.GetProperty("features").EnumerateArray().Single();
        Assert.Equal(7, feature.GetProperty("ordinal").GetInt32());
        Assert.Equal("collect the LLDP table", feature.GetProperty("title").GetString());
        Assert.Equal("ssh", feature.GetProperty("snippet_type").GetString());
    }

    // ─── VendorCommandService writes ────────────────────────────────────

    // Counts command-cache drops — the shared FakeVendorCommandRegistry is a
    // no-op and can't answer that.
    private sealed class RecordingRegistry : flow_weaver_backend.Services.Validation.IVendorCommandRegistry
    {
        public int Invalidated { get; private set; }
        public Task<flow_weaver_backend.Services.Validation.KnownStatus> IsKnownAsync(
            string deviceType, string command, CancellationToken ct)
            => Task.FromResult(default(flow_weaver_backend.Services.Validation.KnownStatus));
        public Task<IReadOnlyList<string>> SuggestSimilarAsync(
            string deviceType, string command, int max, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> KnownCommandsForDeviceTypeAsync(
            string deviceType, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public void Invalidate() => Invalidated++;
    }

    private sealed class VendorFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingRegistry Registry { get; } = new();

        public VendorCommandService Build() => new(
            new VendorCommandRepository(Db),
            new FakeUser(),
            new FakeAudit(),
            new FakeTrace(),
            Registry,
            NullLogger<VendorCommandService>.Instance);

        public Guid Seed(
            string value = "show version", string kind = "exact",
            string deviceType = "cisco_ios", string source = "seed")
        {
            var id = Guid.NewGuid();
            Db.VendorCommands.Add(new VendorCommandModel
            {
                VendorCommandId = id,
                DeviceType = deviceType,
                VendorFamily = "cisco",
                Kind = kind,
                Value = value,
                Source = source,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static string ErrorOf<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result)
    {
        var value = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ObjectResult>(result.Result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value)?.ToString() ?? "";
    }

    [Fact]
    public async Task VendorCommand_UpdatingAnUnknownIdIs404()
    {
        using var f = new VendorFixture();

        var result = await f.Build().UpdateAsync(
            Guid.NewGuid(), new UpdateVendorCommand { Value = "x" });

        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(result.Result);
    }

    // The post-merge state is validated, not just the deltas — blanking a
    // required field via PATCH has to be caught.
    [Theory]
    [InlineData("   ", null, "device_type is required")]
    [InlineData(null, "   ", "value is required")]
    public async Task VendorCommand_TheMergedStateIsValidated(
        string? deviceType, string? value, string expected)
    {
        using var f = new VendorFixture();
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(
            id, new UpdateVendorCommand { DeviceType = deviceType, Value = value });

        Assert.Contains(expected, ErrorOf(result));
    }

    [Fact]
    public async Task VendorCommand_AnUnknownKindIsRejected()
    {
        using var f = new VendorFixture();
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(id, new UpdateVendorCommand { Kind = "fuzzy" });

        Assert.Contains("kind must be", ErrorOf(result));
    }

    // A broken regex would silently degrade the command validator at run
    // time; it is rejected at save time with the parser's own message.
    [Fact]
    public async Task VendorCommand_ABrokenPatternIsRejected()
    {
        using var f = new VendorFixture();
        var id = f.Seed(kind: "pattern", value: "show .*");

        var result = await f.Build().UpdateAsync(
            id, new UpdateVendorCommand { Value = "show [unclosed" });

        Assert.Contains("not a valid regex", ErrorOf(result));
        Assert.Equal("show .*", f.Db.VendorCommands.Single().Value);
    }

    [Fact]
    public async Task VendorCommand_AValidPatternIsAccepted()
    {
        using var f = new VendorFixture();
        var id = f.Seed(kind: "pattern", value: "show .*");

        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Value = "show interface .*" });

        Assert.Equal("show interface .*", f.Db.VendorCommands.Single().Value);
    }

    // Editing a seed row marks it user-managed so a later re-seed does not
    // clobber the admin's change.
    [Fact]
    public async Task VendorCommand_EditingASeedRowMarksItUserManaged()
    {
        using var f = new VendorFixture();
        var id = f.Seed(source: VendorCommandModel.SourceSeed);

        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Notes = "reviewed" });

        Assert.Equal(VendorCommandModel.SourceUser, f.Db.VendorCommands.Single().Source);
    }

    [Fact]
    public async Task VendorCommand_OmittedFieldsAreLeftUntouched()
    {
        using var f = new VendorFixture();
        var id = f.Seed(value: "show version", deviceType: "cisco_ios");

        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Notes = "note" });

        var saved = f.Db.VendorCommands.Single();
        Assert.Equal("show version", saved.Value);
        Assert.Equal("cisco_ios", saved.DeviceType);
        Assert.Equal("note", saved.Notes);
    }

    // A blank note clears it rather than storing whitespace.
    [Fact]
    public async Task VendorCommand_ABlankNoteClearsIt()
    {
        using var f = new VendorFixture();
        var id = f.Seed();
        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Notes = "note" });

        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Notes = "   " });

        Assert.Null(f.Db.VendorCommands.Single().Notes);
    }

    // The catalogue cache has to be dropped or the validator keeps accepting
    // the old command set.
    [Fact]
    public async Task VendorCommand_AnUpdateInvalidatesTheRegistryCache()
    {
        using var f = new VendorFixture();
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateVendorCommand { Notes = "note" });

        Assert.Equal(1, f.Registry.Invalidated);
    }
}


