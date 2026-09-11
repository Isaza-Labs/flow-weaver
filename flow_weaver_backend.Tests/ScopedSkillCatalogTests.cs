using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Skills;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Tests;

// Skills tied to an integration stay out of the prompt until the integration is
// in play. These pin the pieces that decide "in play": the catalog's join, the
// mention matcher, the tool-call → integration lookup, the index the model reads,
// the prompt composition, and the Context round-trip that makes a load stick.
public class ScopedSkillCatalogTests
{
    private static readonly Guid Action1 = Guid.NewGuid();
    private static readonly Guid NetBox = Guid.NewGuid();

    private static ScopedSkill Skill(string name, Guid integration, string integrationName, string slug, string content = "# Title\nbody")
        => new(Guid.NewGuid(), name, ScopedSkillCatalog.TitleOf(content, name), integration, integrationName, slug, content, 100, AlwaysLoaded: false);

    // Maps operation ids to APIs, which is all the catalog asks of the index.
    private sealed class MappingSpecIndex(Dictionary<string, string> opToApi) : IApiSpecIndex
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId)
            => opToApi.TryGetValue(operationId, out var api) ? new ApiOperation { OperationId = operationId, Api = api } : null;
    }

    [Fact]
    public async Task Catalog_lists_only_scoped_rows_and_joins_their_integration()
    {
        using var db = TestDb.NewContext();
        db.Integrations.Add(new Integration { IntegrationId = Action1, Name = "Action1 EU", Slug = "action1", Type = "http", IsActive = true });
        db.AiPromptSkills.AddRange(
            new AiPromptSkill { AiPromptSkillId = Guid.NewGuid(), Name = "action1", Content = "# Action1 — Patch Management\nrules", IntegrationId = Action1, SortOrder = 50, IsActive = true },
            new AiPromptSkill { AiPromptSkillId = Guid.NewGuid(), Name = "house-style", Content = "be brief", IntegrationId = null, IsActive = true },
            new AiPromptSkill { AiPromptSkillId = Guid.NewGuid(), Name = "old", Content = "x", IntegrationId = Action1, IsActive = false });
        await db.SaveChangesAsync();

        var skills = await new ScopedSkillCatalog(db, new MappingSpecIndex([])).ListAsync(CancellationToken.None);

        var only = Assert.Single(skills);
        Assert.Equal("action1", only.Name);
        Assert.Equal("Action1 — Patch Management", only.Title);
        Assert.Equal("Action1 EU", only.IntegrationName);
        Assert.Equal("action1", only.IntegrationSlug);
        Assert.False(only.AlwaysLoaded);
    }

    // A skill whose integration is gone must not vanish with it: it goes back to
    // being global, which is what it was before scoping existed.
    [Fact]
    public async Task A_skill_whose_integration_is_missing_is_always_loaded()
    {
        using var db = TestDb.NewContext();
        db.AiPromptSkills.Add(new AiPromptSkill { AiPromptSkillId = Guid.NewGuid(), Name = "orphan", Content = "ORPHAN TEXT", IntegrationId = Guid.NewGuid(), IsActive = true });
        await db.SaveChangesAsync();

        var skills = await new ScopedSkillCatalog(db, new MappingSpecIndex([])).ListAsync(CancellationToken.None);

        Assert.True(Assert.Single(skills).AlwaysLoaded);
        var prompt = ScopedSkillCatalog.ComposePrompt("base", skills, new HashSet<Guid>());
        Assert.Contains("ORPHAN TEXT", prompt);
        Assert.DoesNotContain("## Integration skills", prompt);
    }

    // execute_operation / operation_detail name an operation, discover_operations
    // an API; both resolve to the integration the spec is attached to.
    [Fact]
    public async Task Tool_calls_resolve_to_the_integration_behind_the_api()
    {
        using var db = TestDb.NewContext();
        db.AiApiSpecs.Add(new AiApiSpec { AiApiSpecId = Guid.NewGuid(), Api = "action1", Content = "openapi: 3.0.0", IntegrationId = Action1, IsActive = true });
        db.AiApiSpecs.Add(new AiApiSpec { AiApiSpecId = Guid.NewGuid(), Api = "fw_runs", Content = "openapi: 3.0.0", IntegrationId = null, IsActive = true });
        await db.SaveChangesAsync();
        var catalog = new ScopedSkillCatalog(db, new MappingSpecIndex(new()
        {
            ["action1:endpoints_managed_list"] = "action1",
            ["fw_runs:list_runs"] = "fw_runs",
        }));

        Assert.Equal(Action1, await catalog.IntegrationForToolCallAsync("execute_operation",
            JsonSerializer.SerializeToElement(new { operation_id = "action1:endpoints_managed_list" }), CancellationToken.None));
        Assert.Equal(Action1, await catalog.IntegrationForToolCallAsync("operation_detail",
            JsonSerializer.SerializeToElement(new { operation_id = "action1:endpoints_managed_list" }), CancellationToken.None));
        Assert.Equal(Action1, await catalog.IntegrationForToolCallAsync("discover_operations",
            JsonSerializer.SerializeToElement(new { api = "action1" }), CancellationToken.None));
        Assert.Null(await catalog.IntegrationForToolCallAsync("execute_operation",
            JsonSerializer.SerializeToElement(new { operation_id = "fw_runs:list_runs" }), CancellationToken.None));
        Assert.Null(await catalog.IntegrationForToolCallAsync("list_workflows",
            JsonSerializer.SerializeToElement(new { }), CancellationToken.None));
    }

    [Fact]
    public void Mentions_match_slug_integration_name_or_skill_name_case_insensitively()
    {
        var skills = new List<ScopedSkill>
        {
            Skill("action1-ops", Action1, "Action1 EU", "action1"),
            Skill("nb", NetBox, "NetBox", "nb"),
        };

        Assert.Contains(Action1, ScopedSkillCatalog.MatchMentions("show me ACTION1 endpoints", skills));
        Assert.Contains(Action1, ScopedSkillCatalog.MatchMentions("use action1-ops please", skills));
        Assert.Contains(NetBox, ScopedSkillCatalog.MatchMentions("sync from netbox", skills));
        Assert.Empty(ScopedSkillCatalog.MatchMentions("what is the weather", skills));
    }

    // "nb" is inside "number"; a two-letter slug must never match on its own.
    [Fact]
    public void Mentions_ignore_needles_shorter_than_three_characters()
    {
        var skills = new List<ScopedSkill> { Skill("nb", NetBox, "NB", "nb") };

        Assert.Empty(ScopedSkillCatalog.MatchMentions("give me the serial number", skills));
    }

    [Fact]
    public void Title_is_the_first_heading_or_the_name()
    {
        Assert.Equal("Action1 — Patch Management & RMM", ScopedSkillCatalog.TitleOf("\n# Action1 — Patch Management & RMM\n\ntext", "x"));
        Assert.Equal("Second level", ScopedSkillCatalog.TitleOf("intro\n## Second level", "x"));
        Assert.Equal("fallback", ScopedSkillCatalog.TitleOf("no heading here", "fallback"));
    }

    [Fact]
    public void Index_lists_every_scoped_skill_and_marks_which_are_loaded()
    {
        var skills = new List<ScopedSkill>
        {
            Skill("action1", Action1, "Action1", "action1", "# Action1 — Patching"),
            Skill("netbox", NetBox, "NetBox", "netbox", "# NetBox — Inventory"),
        };

        var index = ScopedSkillCatalog.RenderIndex(skills, new HashSet<Guid> { Action1 });

        Assert.Contains("`action1` → integration \"Action1\" (`action1`): Action1 — Patching — loaded", index);
        Assert.Contains("`netbox` → integration \"NetBox\" (`netbox`): NetBox — Inventory — not loaded", index);
        Assert.Contains("load_skill", index);
    }

    [Fact]
    public void Prompt_carries_the_index_always_and_the_text_only_when_loaded()
    {
        var skills = new List<ScopedSkill>
        {
            Skill("action1", Action1, "Action1", "action1", "# Action1\nACTION1 RULES"),
            Skill("netbox", NetBox, "NetBox", "netbox", "# NetBox\nNETBOX RULES"),
        };

        var nothing = ScopedSkillCatalog.ComposePrompt("BASE", skills, new HashSet<Guid>());
        Assert.StartsWith("BASE", nothing);
        Assert.Contains("## Integration skills", nothing);
        Assert.DoesNotContain("ACTION1 RULES", nothing);
        Assert.DoesNotContain("NETBOX RULES", nothing);

        var withAction1 = ScopedSkillCatalog.ComposePrompt("BASE", skills, new HashSet<Guid> { Action1 });
        Assert.Contains("ACTION1 RULES", withAction1);
        Assert.Contains("[Integration skill `action1` — applies to integration \"Action1\" (`action1`)]", withAction1);
        Assert.DoesNotContain("NETBOX RULES", withAction1);
    }

    [Fact]
    public void Prompt_without_scoped_skills_is_the_base_prompt_untouched()
    {
        Assert.Equal("BASE\n", ScopedSkillCatalog.ComposePrompt("BASE\n", [], new HashSet<Guid>()));
    }

    // Context was reserved and never written; loaded_skills is its first key. Other
    // keys someone adds later must survive the round-trip.
    [Fact]
    public void Loaded_skills_round_trip_through_the_conversation_context()
    {
        Assert.Empty(ScopedSkillCatalog.LoadedFromContext(default));
        Assert.Empty(ScopedSkillCatalog.LoadedFromContext(TestJson.Element("""{"loaded_skills":"nope"}""")));

        var existing = TestJson.Element("""{"other":{"kept":true},"loaded_skills":["junk"]}""");
        var written = ScopedSkillCatalog.ContextWithLoaded(existing, [Action1, NetBox, Action1]);

        Assert.True(written.GetProperty("other").GetProperty("kept").GetBoolean());
        var back = ScopedSkillCatalog.LoadedFromContext(written);
        Assert.Equal(2, back.Count);
        Assert.Contains(Action1, back);
        Assert.Contains(NetBox, back);
    }
}
