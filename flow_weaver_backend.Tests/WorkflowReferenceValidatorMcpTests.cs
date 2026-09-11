using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Review follow-up: the mcp_server/mcp_tool grant conditions must not be
// bypassable by authoring a workflow. The reference validator now enforces
// mcp.execute (with server/tool context) at save time, in granular mode.
public class WorkflowReferenceValidatorMcpTests
{
    private static AppDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static (WorkflowReferenceValidator v, Guid snippetId, Guid serverId) Setup(
        AppDbContext db, IEffectivePermissions eff, IAppSettingsService settings)
    {
        var snippetId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        db.Snippets.Add(new Snippet
        {
            SnippetId = snippetId, Name = "mcp_call", Type = "mcp_call", IsActive = true,
        });
        db.McpServers.Add(new McpServer
        {
            McpServerId = serverId, Name = "srv", Url = "https://x", Enabled = true, IsActive = true,
        });
        db.SaveChanges();

        var v = new WorkflowReferenceValidator(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new RepositoryBase<IntegrationAction>(db),
            new RepositoryBase<McpServer>(db),
            eff, settings, NullLogger<WorkflowReferenceValidator>.Instance);
        return (v, snippetId, serverId);
    }

    private static JsonElement Nodes(Guid snippetId, Guid serverId, string tool)
        => TestJson.Element($$"""
        [{ "id": "n1", "snippet_id": "{{snippetId}}",
           "config_overrides": { "mcp_server_id": "{{serverId}}", "tool_name": "{{tool}}" } }]
        """);

    [Fact]
    public async Task Denies_save_when_author_lacks_mcp_execute()
    {
        using var db = NewDb(nameof(Denies_save_when_author_lacks_mcp_execute));
        var (v, snippetId, serverId) = Setup(db, new DenyEffective(), new GranularAppSettings());

        var result = await v.ValidateAsync(Nodes(snippetId, serverId, "search"), default);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not authorized") && e.Contains("search"));
    }

    [Fact]
    public async Task Allows_save_when_author_holds_mcp_execute()
    {
        using var db = NewDb(nameof(Allows_save_when_author_holds_mcp_execute));
        var (v, snippetId, serverId) = Setup(db, new AllowEffective(), new GranularAppSettings());

        var result = await v.ValidateAsync(Nodes(snippetId, serverId, "search"), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Skips_mcp_execute_check_in_legacy_mode()
    {
        using var db = NewDb(nameof(Skips_mcp_execute_check_in_legacy_mode));
        // Deny-all effective, but legacy mode ⇒ the save-time mcp.execute gate is
        // skipped (the coarse role gate applies at run time, like integration_action).
        var (v, snippetId, serverId) = Setup(db, new DenyEffective(), new FakeAppSettings());

        var result = await v.ValidateAsync(Nodes(snippetId, serverId, "search"), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Rejects_node_with_snippet_id_that_matches_no_snippet()
    {
        using var db = NewDb(nameof(Rejects_node_with_snippet_id_that_matches_no_snippet));
        var (v, _, _) = Setup(db, new AllowEffective(), new FakeAppSettings());

        // A well-formed but fabricated GUID — the exact shape the agent produced
        // when it invented ids instead of resolving them via list_snippets.
        var fakeId = Guid.NewGuid();
        var nodes = TestJson.Element($$"""
            [{ "id": "find-dashboard", "snippet_id": "{{fakeId}}",
               "config_overrides": { "format": "html" } }]
            """);

        var result = await v.ValidateAsync(nodes, default);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors,
            e => e.Contains("find-dashboard") && e.Contains("does not match any snippet"));
    }

    [Fact]
    public async Task Allows_node_referencing_a_real_non_integration_snippet()
    {
        using var db = NewDb(nameof(Allows_node_referencing_a_real_non_integration_snippet));
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new Snippet
        {
            SnippetId = snippetId, Name = "report", Type = "report", IsActive = true,
        });
        db.SaveChanges();
        var v = new WorkflowReferenceValidator(
            new SnippetRepository(db), new IntegrationRepository(db),
            new RepositoryBase<IntegrationAction>(db), new RepositoryBase<McpServer>(db),
            new AllowEffective(), new FakeAppSettings(), NullLogger<WorkflowReferenceValidator>.Instance);

        var nodes = TestJson.Element($$"""
            [{ "id": "build-report", "snippet_id": "{{snippetId}}",
               "config_overrides": { "format": "html" } }]
            """);

        var result = await v.ValidateAsync(nodes, default);

        Assert.True(result.IsValid);
    }
}

file sealed class DenyEffective : IEffectivePermissions
{
    public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}

file sealed class AllowEffective : IEffectivePermissions
{
    public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { "mcp.execute" });
}

file sealed class GranularAppSettings : IAppSettingsService
{
    public Task<AppSettings> GetAsync(CancellationToken ct = default)
        => Task.FromResult(new AppSettings { RbacMode = "granular" });
    public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
        => Task.FromResult(updated);
}
