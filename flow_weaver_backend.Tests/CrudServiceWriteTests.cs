using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AiPromptSkill;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Integration;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using PromptSkillModel = flow_weaver_backend.Models.AiPromptSkill;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

// The write half of three CRUD services. What is worth pinning here is not the
// field copying — it is the guards around it: which role may set a privileged
// flag, which mutations are audited as a distinct event, and where PATCH
// semantics silently differ between endpoints (omitting a field usually keeps
// it; on prompt skills the integration link is authoritative and null unlinks).
public class CrudServiceWriteTests
{

    private static FakeUser Caller(params string[] roles)
        => new() { Roles = roles.Length == 0 ? new[] { "operator" } : roles };

    // Records every audit action so the "this mutation gets its own audit
    // row" invariants can be asserted.
    private sealed class RecordingAudit : flow_weaver_backend.Services.Audit.IAuditLogger
    {
        public List<string> Actions { get; } = new();
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    // The services return several ObjectResult subclasses (BadRequest,
    // NotFound, Conflict, plain 403), so these read through the base type
    // rather than asserting an exact one.
    private static string ProblemOrError(ActionResult result)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var value = obj.Value!;
        var prop = value.GetType().GetProperty("error");
        return prop is null ? value.ToString() ?? "" : prop.GetValue(value)?.ToString() ?? "";
    }

    private static string ErrorOf<T>(ActionResult<T> result)
        => ProblemOrError(result.Result!);

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 0;

    // ─── SnippetService ─────────────────────────────────────────────────

    private sealed class SnippetFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingAudit Audit { get; } = new();
        public FakeUser CurrentCaller { get; set; } = Caller();

        public SnippetService Build() => new(
            new SnippetRepository(Db),
            new StepRunRepository(Db),
            CurrentCaller,
            Audit,
            new FakeTrace(),
            NullLogger<SnippetService>.Instance);

        public Guid Seed(bool networkEnabled = false, string type = "python_snippet", string? code = "print(1)")
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new SnippetModel
            {
                SnippetId = id,
                Name = "collect",
                Type = type,
                Code = code,
                TargetMode = "once",
                NetworkEnabled = networkEnabled,
                LogicDiagramMermaid = Diagram,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    // python/transform types additionally require a Mermaid logic diagram, so
    // the builder supplies one by default — otherwise every network_enabled
    // test would trip that unrelated validation first.
    private const string Diagram = "graph TD\n  A-->B";

    private static CreateSnippet NewSnippet(
        string name = "collect", string type = "ssh", string targetMode = "once",
        bool? networkEnabled = null, string? code = null)
        => new()
        {
            Name = name,
            Type = type,
            TargetMode = targetMode,
            NetworkEnabled = networkEnabled,
            Code = code,
            LogicDiagramMermaid = Diagram,
        };

    [Theory]
    [InlineData("", "ssh", "once", "name is required")]
    [InlineData("collect", "", "once", "type is required")]
    [InlineData("collect", "ssh", "", "target_mode is required")]
    public async Task Snippet_CreateRequiresTheThreeCoreFields(
        string name, string type, string targetMode, string expected)
    {
        using var f = new SnippetFixture();

        var result = await f.Build().PostAsync(NewSnippet(name, type, targetMode));

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(f.Db.Snippets);
    }

    [Fact]
    public async Task Snippet_CreatePersistsTheRowWithTheDefaults()
    {
        using var f = new SnippetFixture();

        var result = await f.Build().PostAsync(NewSnippet());

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.Snippets);
        Assert.Equal("collect", saved.Name);
        Assert.True(saved.IsActive);
        Assert.False(saved.NetworkEnabled);
        Assert.Contains("create", f.Audit.Actions);
    }

    // network_enabled lifts the sandbox's network isolation, so it is
    // admin-only AND python-only. Both halves of the guard matter: an
    // operator must not be able to grant themselves host networking, and the
    // flag is meaningless on a non-python type.
    [Fact]
    public async Task Snippet_NetworkEnabledIsRefusedForNonAdmins()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("operator") };

        var result = await f.Build().PostAsync(
            NewSnippet(type: "python_snippet", networkEnabled: true));

        Assert.Equal(403, StatusOf(result));
        Assert.Empty(f.Db.Snippets);
    }

    [Fact]
    public async Task Snippet_NetworkEnabledOnlyAppliesToPythonSnippets()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("admin") };

        var result = await f.Build().PostAsync(NewSnippet(type: "ssh", networkEnabled: true));

        Assert.Contains("only applies to python_snippet", ErrorOf(result));
    }

    [Fact]
    public async Task Snippet_AnAdminMaySetNetworkEnabledOnAPythonSnippet()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("admin") };

        var result = await f.Build().PostAsync(
            NewSnippet(type: "python_snippet", networkEnabled: true, code: "import netmiko"));

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.True(Assert.Single(f.Db.Snippets).NetworkEnabled);
    }

    [Fact]
    public async Task Snippet_UpdatingAnUnknownIdIs404()
    {
        using var f = new SnippetFixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateSnippet { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // A network-enabled snippet is privileged as a whole: even editing its
    // code requires admin, because the code is what runs with the isolation
    // lifted.
    [Fact]
    public async Task Snippet_ANonAdminCannotEditANetworkEnabledSnippetAtAll()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("operator") };
        var id = f.Seed(networkEnabled: true);

        var result = await f.Build().UpdateAsync(id, new UpdateSnippet { Code = "print(2)" });

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("print(1)", f.Db.Snippets.Single().Code);
    }

    [Fact]
    public async Task Snippet_AnAdminCanEditANetworkEnabledSnippet()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("admin") };
        var id = f.Seed(networkEnabled: true);

        await f.Build().UpdateAsync(id, new UpdateSnippet { Code = "print(2)" });

        Assert.Equal("print(2)", f.Db.Snippets.Single().Code);
    }

    // Turning the flag ON via update goes through the same guard as create.
    [Fact]
    public async Task Snippet_ANonAdminCannotTurnNetworkEnabledOn()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("operator") };
        var id = f.Seed(networkEnabled: false);

        var result = await f.Build().UpdateAsync(id, new UpdateSnippet { NetworkEnabled = true });

        Assert.Equal(403, StatusOf(result));
        Assert.False(f.Db.Snippets.Single().NetworkEnabled);
    }

    // Turning it OFF is always allowed — de-escalation needs no privilege.
    [Fact]
    public async Task Snippet_TurningNetworkEnabledOffNeedsNoPrivilege()
    {
        using var f = new SnippetFixture { CurrentCaller = Caller("admin") };
        var id = f.Seed(networkEnabled: true);

        await f.Build().UpdateAsync(id, new UpdateSnippet { NetworkEnabled = false });

        Assert.False(f.Db.Snippets.Single().NetworkEnabled);
    }

    // PATCH semantics: an omitted field keeps its current value.
    [Fact]
    public async Task Snippet_OmittedFieldsAreLeftUntouched()
    {
        using var f = new SnippetFixture();
        var id = f.Seed(type: "ssh", code: "show version");

        await f.Build().UpdateAsync(id, new UpdateSnippet { Description = "collects the version" });

        var saved = f.Db.Snippets.Single();
        Assert.Equal("collect", saved.Name);
        Assert.Equal("show version", saved.Code);
        Assert.Equal("collects the version", saved.Description);
    }

    // Scaffold detection re-runs on update, because the common "edit with AI"
    // flow swaps only Code — a placeholder body would otherwise slip in.
    [Fact]
    public async Task Snippet_ScaffoldCodeIsRejectedOnUpdateToo()
    {
        using var f = new SnippetFixture();
        var id = f.Seed(type: "python_snippet");

        var result = await f.Build().UpdateAsync(
            id, new UpdateSnippet { Code = "# TODO: implement" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── IntegrationService ─────────────────────────────────────────────

    private sealed class IntegrationFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public RecordingAudit Audit { get; } = new();
        public bool Granular { get; set; }
        public bool Allow { get; set; } = true;

        public IntegrationService Build() => new(
            new IntegrationRepository(Db),
            Caller(),
            Audit,
            new FakeTrace(),
            Allow ? new AllowPerms() : new DenyAllPermissions(),
            new GatedSettings { Granular = Granular },
            new FakeOAuthTokens(),
            NullLogger<IntegrationService>.Instance);

        public Guid Seed(bool allowPrivate = false)
        {
            var id = Guid.NewGuid();
            Db.Integrations.Add(new IntegrationModel
            {
                IntegrationId = id,
                Name = "Jira",
                Type = "generic_rest",
                BaseURL = "https://jira.test",
                AuthConfig = TestJson.Element("{}"),
                Headers = TestJson.Element("{}"),
                HealthCheck = TestJson.Element("{}"),
                Status = IntegrationStatus.Healthy,
                AllowPrivateNetwork = allowPrivate,
                Enabled = true,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class AllowPerms : flow_weaver_backend.Services.Permission.IResourcePermissionService
    {
        public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(string t, Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ResourcePermissionResponse> GrantAsync(
            string t, Guid id, GrantResourcePermissionRequest dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RevokeAsync(Guid permissionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasAtLeastAsync(string t, Guid id, string requiredRole, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class GatedSettings : flow_weaver_backend.Services.Settings.IAppSettingsService
    {
        public bool Granular { get; init; }
        public Task<flow_weaver_backend.Services.Settings.AppSettings> GetAsync(
            CancellationToken ct = default)
            => Task.FromResult(flow_weaver_backend.Services.Settings.AppSettings.Default with
            {
                PermissionsGranularGatingEnabled = Granular,
            });
        public Task<flow_weaver_backend.Services.Settings.AppSettings> UpdateAsync(
            flow_weaver_backend.Services.Settings.AppSettings updated,
            CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    [Theory]
    [InlineData("", "generic_rest", "https://x.test", "name is required")]
    [InlineData("Jira", "", "https://x.test", "type is required")]
    [InlineData("Jira", "generic_rest", "", "base_url is required")]
    public async Task Integration_CreateRequiresTheThreeCoreFields(
        string name, string type, string baseUrl, string expected)
    {
        using var f = new IntegrationFixture();

        var result = await f.Build().PostAsync(new CreateIntegration
        {
            Name = name, Type = type, BaseURL = baseUrl,
        });

        Assert.Equal(expected, ErrorOf(result));
        Assert.Empty(f.Db.Integrations);
    }

    [Fact]
    public async Task Integration_CreatePersistsTheRow()
    {
        using var f = new IntegrationFixture();

        var result = await f.Build().PostAsync(new CreateIntegration
        {
            Name = "Jira", Type = "generic_rest", BaseURL = "https://jira.test",
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.Integrations);
        Assert.Contains("create", f.Audit.Actions);
    }

    // allow_private_network is the SSRF-guard opt-out. Every activation gets
    // a dedicated audit row so /admin/audit can list them regardless of which
    // mutation caused it.
    [Fact]
    public async Task Integration_EnablingThePrivateNetworkOptOutOnCreateIsAuditedSeparately()
    {
        using var f = new IntegrationFixture();

        await f.Build().PostAsync(new CreateIntegration
        {
            Name = "Internal", Type = "generic_rest", BaseURL = "http://10.0.0.5",
            AllowPrivateNetwork = true,
        });

        Assert.Contains("allow_private_network.enabled", f.Audit.Actions);
    }

    [Fact]
    public async Task Integration_NotEnablingTheOptOutWritesNoExtraAuditRow()
    {
        using var f = new IntegrationFixture();

        await f.Build().PostAsync(new CreateIntegration
        {
            Name = "Jira", Type = "generic_rest", BaseURL = "https://jira.test",
        });

        Assert.DoesNotContain("allow_private_network.enabled", f.Audit.Actions);
    }

    [Fact]
    public async Task Integration_TogglingTheOptOutOnUpdateIsAuditedInBothDirections()
    {
        using var f = new IntegrationFixture();
        var id = f.Seed(allowPrivate: false);
        var service = f.Build();

        await service.UpdateAsync(id, new UpdateIntegration
        {
            AllowPrivateNetwork = true,
            AllowPrivateNetworkReason = "  reaches the lab jumphost  ",
        });
        Assert.Contains("allow_private_network.enabled", f.Audit.Actions);

        await service.UpdateAsync(id, new UpdateIntegration { AllowPrivateNetwork = false });
        Assert.Contains("allow_private_network.disabled", f.Audit.Actions);
    }

    // An update that doesn't change the flag must not emit the dedicated row
    // — otherwise the audit filter fills with noise.
    [Fact]
    public async Task Integration_AnUnchangedOptOutEmitsNoDedicatedAuditRow()
    {
        using var f = new IntegrationFixture();
        var id = f.Seed(allowPrivate: true);

        await f.Build().UpdateAsync(id, new UpdateIntegration
        {
            Name = "Jira Cloud", AllowPrivateNetwork = true,
        });

        Assert.DoesNotContain("allow_private_network.enabled", f.Audit.Actions);
        Assert.Contains("update", f.Audit.Actions);
    }

    [Fact]
    public async Task Integration_UpdatingAnUnknownIdIs404()
    {
        using var f = new IntegrationFixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateIntegration { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // With granular gating on, editing needs a per-resource editor grant.
    [Fact]
    public async Task Integration_UpdateWithoutAnEditorGrantIs403()
    {
        using var f = new IntegrationFixture { Granular = true, Allow = false };
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(id, new UpdateIntegration { Name = "renamed" });

        Assert.Equal(403, StatusOf(result));
        Assert.Equal("Jira", f.Db.Integrations.Single().Name);
    }

    // Granular gating is opt-in — with it off, the same denying service
    // must not block the edit.
    [Fact]
    public async Task Integration_UpdateIsUngatedWhenGranularGatingIsNotEnabled()
    {
        using var f = new IntegrationFixture { Granular = false, Allow = false };
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateIntegration { Name = "renamed" });

        Assert.Equal("renamed", f.Db.Integrations.Single().Name);
    }

    // Delete is a soft delete and needs the stricter owner role.
    [Fact]
    public async Task Integration_DeleteSoftDeletesTheRow()
    {
        using var f = new IntegrationFixture();
        var id = f.Seed();

        await f.Build().DeleteAsync(id);

        Assert.False(f.Db.Integrations.Single().IsActive);
        Assert.Contains("delete", f.Audit.Actions);
    }

    [Fact]
    public async Task Integration_DeleteWithoutAnOwnerGrantIs403()
    {
        using var f = new IntegrationFixture { Granular = true, Allow = false };
        var id = f.Seed();

        var result = await f.Build().DeleteAsync(id);

        Assert.Equal(403, StatusOf(result));
        Assert.True(f.Db.Integrations.Single().IsActive);
    }

    [Fact]
    public async Task Integration_DeletingAnUnknownIdIs404()
    {
        using var f = new IntegrationFixture();

        var result = await f.Build().DeleteAsync(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // ─── AiPromptSkillService ───────────────────────────────────────────

    private sealed class CountingLoader : ISkillPromptLoader
    {
        public int Invalidations { get; private set; }
        public Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default)
            => Task.FromResult("");
        public void Invalidate() => Invalidations++;
    }

    private sealed class SkillFixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public CountingLoader Loader { get; } = new();

        public AiPromptSkillService Build() => new(
            new AiPromptSkillRepository(Db),
            Caller(),
            Loader,
            NullLogger<AiPromptSkillService>.Instance);

        public Guid Seed(string name = "ssh.md", bool active = true, string content = "# ssh")
        {
            var id = Guid.NewGuid();
            Db.AiPromptSkills.Add(new PromptSkillModel
            {
                AiPromptSkillId = id,
                Name = name,
                Content = content,
                SortOrder = 100,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("", "body", "name is required")]
    [InlineData("   ", "body", "name is required")]
    [InlineData("no-extension", "body", "name must match")]
    [InlineData("bad name.md", "body", "name must match")]
    public async Task Skill_CreateValidatesTheFilename(string name, string content, string expected)
    {
        using var f = new SkillFixture();

        var result = await f.Build().PostAsync(new CreateAiPromptSkill { Name = name, Content = content });

        Assert.Contains(expected, ErrorOf(result));
        Assert.Empty(f.Db.AiPromptSkills);
    }

    [Fact]
    public async Task Skill_CreatePersistsAndInvalidatesTheCache()
    {
        using var f = new SkillFixture();

        var result = await f.Build().PostAsync(
            new CreateAiPromptSkill { Name = " ssh.md ", Content = "# ssh" });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var saved = Assert.Single(f.Db.AiPromptSkills);
        // The name is trimmed so " ssh.md " and "ssh.md" are one slot.
        Assert.Equal("ssh.md", saved.Name);
        Assert.Equal(1, f.Loader.Invalidations);
    }

    // base.md leads the assembled prompt; everything else defaults behind it.
    [Theory]
    [InlineData("ssh.md", 100)]
    public async Task Skill_TheDefaultSortOrderPutsBaseFirst(string name, int expected)
    {
        using var f = new SkillFixture();

        await f.Build().PostAsync(new CreateAiPromptSkill { Name = name, Content = "x" });

        Assert.Equal(expected, f.Db.AiPromptSkills.Single().SortOrder);
    }

    [Fact]
    public async Task Skill_AnExplicitSortOrderWins()
    {
        using var f = new SkillFixture();

        await f.Build().PostAsync(new CreateAiPromptSkill
        {
            Name = "ssh.md", Content = "x", SortOrder = 5,
        });

        Assert.Equal(5, f.Db.AiPromptSkills.Single().SortOrder);
    }

    [Fact]
    public async Task Skill_CreatingADuplicateNameIs409()
    {
        using var f = new SkillFixture();
        f.Seed("ssh.md");

        var result = await f.Build().PostAsync(new CreateAiPromptSkill { Name = "ssh.md", Content = "x" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Single(f.Db.AiPromptSkills);
    }

    // Upsert: the frontend shows one logical slot per filename, so recreating
    // a previously-deleted name reactivates that row rather than tripping the
    // (Name) unique index.
    [Fact]
    public async Task Skill_RecreatingASoftDeletedNameReactivatesTheSameRow()
    {
        using var f = new SkillFixture();
        var id = f.Seed("ssh.md", active: false, content: "old");

        var result = await f.Build().PostAsync(
            new CreateAiPromptSkill { Name = "ssh.md", Content = "new" });

        Assert.Null(result.Result);
        var saved = Assert.Single(f.Db.AiPromptSkills);
        Assert.Equal(id, saved.AiPromptSkillId);
        Assert.True(saved.IsActive);
        Assert.Equal("new", saved.Content);
    }

    [Fact]
    public async Task Skill_UpdatingAnUnknownIdIs404()
    {
        using var f = new SkillFixture();

        var result = await f.Build().UpdateAsync(Guid.NewGuid(), new UpdateAiPromptSkill { Content = "x" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Skill_RenamingValidatesThePattern()
    {
        using var f = new SkillFixture();
        var id = f.Seed();

        var result = await f.Build().UpdateAsync(id, new UpdateAiPromptSkill { Name = "not-markdown" });

        Assert.Contains("name must match", ErrorOf(result));
        Assert.Equal("ssh.md", f.Db.AiPromptSkills.Single().Name);
    }

    [Fact]
    public async Task Skill_RenamingOntoAnotherSkillsNameIs409()
    {
        using var f = new SkillFixture();
        f.Seed("ssh.md");
        var id = f.Seed("http.md");

        var result = await f.Build().UpdateAsync(id, new UpdateAiPromptSkill { Name = "ssh.md" });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    // Renaming to the value it already has is a no-op, not a self-collision.
    [Fact]
    public async Task Skill_RenamingToItsOwnNameIsAccepted()
    {
        using var f = new SkillFixture();
        var id = f.Seed("ssh.md");

        var result = await f.Build().UpdateAsync(id, new UpdateAiPromptSkill { Name = "ssh.md" });

        Assert.Null(result.Result);
    }

    [Fact]
    public async Task Skill_UpdateInvalidatesTheCachedPrompt()
    {
        using var f = new SkillFixture();
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateAiPromptSkill { Content = "# updated" });

        Assert.Equal("# updated", f.Db.AiPromptSkills.Single().Content);
        Assert.Equal(1, f.Loader.Invalidations);
    }

    // The integration link is authoritative rather than PATCH-style: the
    // admin form always sends the picker value, so omitting it unlinks.
    [Fact]
    public async Task Skill_TheIntegrationLinkIsAuthoritativeSoOmittingItUnlinks()
    {
        using var f = new SkillFixture();
        var id = f.Seed();
        var service = f.Build();
        await service.UpdateAsync(id, new UpdateAiPromptSkill { IntegrationId = Guid.NewGuid() });
        Assert.NotNull(f.Db.AiPromptSkills.Single().IntegrationId);

        await service.UpdateAsync(id, new UpdateAiPromptSkill { Content = "unrelated edit" });

        Assert.Null(f.Db.AiPromptSkills.Single().IntegrationId);
    }

    [Fact]
    public async Task Skill_DeactivatingViaUpdateIsSupported()
    {
        using var f = new SkillFixture();
        var id = f.Seed();

        await f.Build().UpdateAsync(id, new UpdateAiPromptSkill { IsActive = false });

        Assert.False(f.Db.AiPromptSkills.Single().IsActive);
    }
}



