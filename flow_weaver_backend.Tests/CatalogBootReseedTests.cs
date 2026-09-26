using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Seed;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The boot sync used to overwrite every catalog row named like a shipped /Skills or /Specs
// file on EVERY restart, and re-activate it: an admin's edit to a shipped skill survived only
// until the next deploy, and a skill an admin switched off came back on. These pin the rule
// that replaced it (ShippedCatalog): a shipped update moves a row forward only while the row
// still holds the shipped content the sync last wrote.
public class CatalogBootReseedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fw-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _sp;

    public CatalogBootReseedTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Skills"));
        Directory.CreateDirectory(Path.Combine(_root, "Specs"));
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Ship(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(_root, relativePath), content);

    private Task Boot() => CatalogBootReseedService.ReseedAllAsync(
        _sp.GetRequiredService<IServiceScopeFactory>(), new Env(_root), NullLogger.Instance);

    private AppDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<AiPromptSkill> Skill(string name)
    {
        using var db = Db();
        return await db.AiPromptSkills.AsNoTracking().SingleAsync(s => s.Name == name);
    }

    private async Task EditSkill(string name, Action<AiPromptSkill> edit)
    {
        using var db = Db();
        var row = await db.AiPromptSkills.SingleAsync(s => s.Name == name);
        edit(row);
        await db.SaveChangesAsync();
    }

    // ─── Boot sync ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_shipped_skill_is_inserted_with_its_shipped_hash()
    {
        Ship("Skills/git.md", "v1");

        await Boot();

        var row = await Skill("git.md");
        Assert.Equal("v1", row.Content);
        Assert.True(row.IsActive);
        Assert.Equal(ShippedCatalog.Hash("v1"), row.ShippedContentHash);
    }

    [Fact]
    public async Task An_unedited_shipped_skill_follows_the_new_shipped_version()
    {
        Ship("Skills/git.md", "v1");
        await Boot();

        Ship("Skills/git.md", "v2");
        await Boot();

        var row = await Skill("git.md");
        Assert.Equal("v2", row.Content);
        Assert.Equal(ShippedCatalog.Hash("v2"), row.ShippedContentHash);
    }

    [Fact]
    public async Task An_admin_edit_survives_a_restart()
    {
        Ship("Skills/git.md", "v1");
        await Boot();
        await EditSkill("git.md", r => r.Content = "admin version");

        await Boot();

        Assert.Equal("admin version", (await Skill("git.md")).Content);
    }

    [Fact]
    public async Task An_admin_edit_survives_an_upgrade_that_ships_a_new_version()
    {
        Ship("Skills/git.md", "v1");
        await Boot();
        await EditSkill("git.md", r => r.Content = "admin version");

        Ship("Skills/git.md", "v2");
        await Boot();

        var row = await Skill("git.md");
        Assert.Equal("admin version", row.Content);
        Assert.Equal(ShippedCatalog.Hash("v1"), row.ShippedContentHash); // still records what was shipped
    }

    [Fact]
    public async Task A_skill_an_admin_deactivated_stays_off()
    {
        Ship("Skills/git.md", "v1");
        await Boot();
        await EditSkill("git.md", r => r.IsActive = false);

        Ship("Skills/git.md", "v2");
        await Boot();

        var row = await Skill("git.md");
        Assert.False(row.IsActive);
        Assert.Equal("v2", row.Content); // content still moves forward: it was not edited
    }

    [Fact]
    public async Task A_row_from_before_hashes_existed_takes_the_shipped_version_once_and_is_tracked_after()
    {
        using (var db = Db())
        {
            db.AiPromptSkills.Add(new AiPromptSkill
            {
                AiPromptSkillId = Guid.NewGuid(), Name = "git.md", Content = "old shipped", IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        Ship("Skills/git.md", "v2");

        await Boot();
        Assert.Equal("v2", (await Skill("git.md")).Content);

        await EditSkill("git.md", r => r.Content = "admin version");
        await Boot();
        Assert.Equal("admin version", (await Skill("git.md")).Content);
    }

    [Fact]
    public async Task A_row_an_admin_created_is_never_overwritten_by_a_shipped_file_of_the_same_name()
    {
        using (var db = Db())
        {
            db.AiPromptSkills.Add(new AiPromptSkill
            {
                AiPromptSkillId = Guid.NewGuid(), Name = "netbox.md", Content = "ours",
                CreatedBy = Guid.NewGuid(), IsActive = true,
            });
            await db.SaveChangesAsync();
        }
        Ship("Skills/netbox.md", "shipped later");

        await Boot();

        var row = await Skill("netbox.md");
        Assert.Equal("ours", row.Content);
        Assert.Null(row.ShippedContentHash);
    }

    [Fact]
    public async Task An_edited_spec_survives_an_upgrade_too()
    {
        Ship("Specs/netbox.yaml", "openapi: 3.0.0\npaths: {}\n");
        await Boot();
        using (var db = Db())
        {
            var row = await db.AiApiSpecs.SingleAsync(s => s.Api == "netbox");
            row.Content = "openapi: 3.0.0\npaths:\n  /custom:\n    get: {}\n";
            await db.SaveChangesAsync();
        }

        Ship("Specs/netbox.yaml", "openapi: 3.1.0\npaths: {}\n");
        await Boot();

        using var check = Db();
        var spec = await check.AiApiSpecs.AsNoTracking().SingleAsync(s => s.Api == "netbox");
        Assert.Contains("/custom", spec.Content);
    }

    // ─── Explicit "Reseed from disk" ────────────────────────────────────

    [Fact]
    public async Task Reseed_from_disk_takes_the_shipped_version_and_boots_track_it_again()
    {
        Ship("Skills/git.md", "v1");
        await Boot();
        await EditSkill("git.md", r => r.Content = "admin version");

        using (var db = Db())
        {
            var reseed = new CatalogReseedService(db, new Env(_root), new FakeUser(),
                new NoopSkillLoader(), new NoopSpecIndex(), NullLogger<CatalogReseedService>.Instance);
            await reseed.ReseedSkillsAsync(CancellationToken.None);
        }
        Assert.Equal("v1", (await Skill("git.md")).Content);

        Ship("Skills/git.md", "v2");
        await Boot();
        Assert.Equal("v2", (await Skill("git.md")).Content);
    }

    // ─── The rule itself ────────────────────────────────────────────────

    [Theory]
    [InlineData("v1", "v1", false, "v1", "Unchanged")]
    [InlineData("v1", null, false, "v1", "RecordHash")]
    [InlineData("v1", "v1", false, "v2", "Update")]
    [InlineData("edit", "v1", false, "v2", "KeepEdited")]
    [InlineData("old", null, false, "v2", "Update")]
    [InlineData("ours", null, true, "v2", "KeepEdited")]
    [InlineData("v2", "v1", false, "v2", "RecordHash")] // edited into the new version
    public void Decide(string content, string? shippedAs, bool createdByAdmin, string disk, string expected)
    {
        var hash = shippedAs is null ? null : ShippedCatalog.Hash(shippedAs);
        var createdBy = createdByAdmin ? Guid.NewGuid() : (Guid?)null;

        Assert.Equal(Enum.Parse<ShippedCatalog.Decision>(expected), ShippedCatalog.Decide(content, hash, createdBy, disk));
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NoopSkillLoader : ISkillPromptLoader
    {
        public Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default) => Task.FromResult("");
        public void Invalidate() { }
    }

    private sealed class NoopSpecIndex : IApiSpecIndex
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) => null;
    }
}
