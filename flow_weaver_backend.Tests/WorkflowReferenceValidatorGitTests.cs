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

// A git step runs in the worker with no user behind it, so the only place its
// author can be checked is at save. Repository writes are git.manage (Admin
// tier) on every other surface; a workflow must not be a way to get them with
// only workflow.update.
public class WorkflowReferenceValidatorGitTests
{
    private static AppDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static (WorkflowReferenceValidator v, Guid snippetId) Setup(
        string dbName, string[] roles, bool holdsGitManage, string rbacMode = "legacy")
    {
        var db = NewDb(dbName);
        var snippetId = Guid.NewGuid();
        db.Snippets.Add(new Snippet { SnippetId = snippetId, Name = "git", Type = "git", IsActive = true });
        db.SaveChanges();

        var v = new WorkflowReferenceValidator(
            new SnippetRepository(db),
            new IntegrationRepository(db),
            new RepositoryBase<IntegrationAction>(db),
            new RepositoryBase<McpServer>(db),
            new ScriptedEffective(holdsGitManage),
            new ModeSettings(rbacMode),
            new FakeUser { Roles = roles },
            NullLogger<WorkflowReferenceValidator>.Instance);
        return (v, snippetId);
    }

    private static JsonElement GitNode(Guid snippetId, string? operationJson)
    {
        var overrides = operationJson is null
            ? "{}"
            : "{\"operation\":" + operationJson + ",\"repository_id\":\"" + Guid.NewGuid() + "\"}";
        return TestJson.Element(
            "[{\"id\":\"git-step\",\"snippet_id\":\"" + snippetId + "\",\"config_overrides\":" + overrides + "}]");
    }

    [Theory]
    [InlineData("\"write_file\"")]
    [InlineData("\"commit\"")]
    [InlineData("\"pull\"")]
    [InlineData("\"push\"")]
    [InlineData("\"{{ input.op }}\"")]
    public async Task AnOperatorCannotSaveAGitWrite(string op)
    {
        var (v, sid) = Setup(nameof(AnOperatorCannotSaveAGitWrite) + op, new[] { "operator" }, holdsGitManage: true);

        var result = await v.ValidateAsync(GitNode(sid, op), default);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("git-step") && e.Contains("git.manage"));
    }

    [Fact]
    public async Task AnOperatorCanSaveAGitRead()
    {
        var (v, sid) = Setup(nameof(AnOperatorCanSaveAGitRead), new[] { "operator" }, holdsGitManage: false);

        var result = await v.ValidateAsync(GitNode(sid, "\"read_file\""), default);

        Assert.True(result.IsValid);
    }

    // Dropping a git node and saving before choosing the operation must not be
    // blocked; the handler rejects an empty operation at run time.
    [Fact]
    public async Task AGitNodeWithoutAnOperationIsNotAWrite()
    {
        var (v, sid) = Setup(nameof(AGitNodeWithoutAnOperationIsNotAWrite), new[] { "operator" }, holdsGitManage: false);

        var result = await v.ValidateAsync(GitNode(sid, null), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task AnAdminCanSaveAGitWrite()
    {
        var (v, sid) = Setup(nameof(AnAdminCanSaveAGitWrite), new[] { "admin" }, holdsGitManage: true);

        var result = await v.ValidateAsync(GitNode(sid, "\"push\""), default);

        Assert.True(result.IsValid);
    }

    // A transport ceiling caps admin too: EffectivePermissions says no.
    [Fact]
    public async Task AnAdminCappedBelowGitManageCannotSaveAGitWrite()
    {
        var (v, sid) = Setup(nameof(AnAdminCappedBelowGitManageCannotSaveAGitWrite), new[] { "admin" }, holdsGitManage: false);

        var result = await v.ValidateAsync(GitNode(sid, "\"commit\""), default);

        Assert.False(result.IsValid);
    }

    // Legacy mode ignores custom grants: git.manage stays admin-only.
    [Fact]
    public async Task LegacyModeIgnoresAGitManageGrantOnAnOperator()
    {
        var (v, sid) = Setup(nameof(LegacyModeIgnoresAGitManageGrantOnAnOperator), new[] { "operator" }, holdsGitManage: true);

        var result = await v.ValidateAsync(GitNode(sid, "\"write_file\""), default);

        Assert.False(result.IsValid);
    }

    // Granular mode honours the grant.
    [Fact]
    public async Task GranularModeHonoursAGitManageGrant()
    {
        var (v, sid) = Setup(nameof(GranularModeHonoursAGitManageGrant), new[] { "operator" }, holdsGitManage: true, rbacMode: "granular");

        var result = await v.ValidateAsync(GitNode(sid, "\"write_file\""), default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task GranularModeWithoutTheGrantBlocksTheWrite()
    {
        var (v, sid) = Setup(nameof(GranularModeWithoutTheGrantBlocksTheWrite), new[] { "operator" }, holdsGitManage: false, rbacMode: "granular");

        var result = await v.ValidateAsync(GitNode(sid, "\"push\""), default);

        Assert.False(result.IsValid);
    }

    // ─── only what the edit ADDS is gated ───────────────────────────────

    // An operator editing an admin-authored workflow must not be blocked by a
    // git write that is already saved on that node.
    [Fact]
    public async Task AnOperatorCanSaveAWorkflowWhoseGitWriteWasAlreadyThere()
    {
        var (v, sid) = Setup(nameof(AnOperatorCanSaveAWorkflowWhoseGitWriteWasAlreadyThere), new[] { "operator" }, holdsGitManage: false);
        var nodes = GitNode(sid, "\"push\"");

        var result = await v.ValidateAsync(nodes, default, previousNodes: nodes);

        Assert.True(result.IsValid);
    }

    // Turning a read node into a write one is a new write.
    [Fact]
    public async Task TurningAReadNodeIntoAWriteIsStillGated()
    {
        var (v, sid) = Setup(nameof(TurningAReadNodeIntoAWriteIsStillGated), new[] { "operator" }, holdsGitManage: false);

        var result = await v.ValidateAsync(
            GitNode(sid, "\"commit\""), default, previousNodes: GitNode(sid, "\"read_file\""));

        Assert.False(result.IsValid);
    }

    // Changing the operation AND the repository of a saved write node is a new
    // write to a new place — the exemption must not cover it.
    [Fact]
    public async Task ChangingTheOperationAndTheRepositoryIsGated()
    {
        var (v, sid) = Setup(nameof(ChangingTheOperationAndTheRepositoryIsGated), new[] { "operator" }, holdsGitManage: false);
        var before = TestJson.Element(
            "[{\"id\":\"git-step\",\"snippet_id\":\"" + sid + "\",\"config_overrides\":{\"operation\":\"pull\",\"repository_id\":\"" + Guid.NewGuid() + "\"}}]");
        var after = TestJson.Element(
            "[{\"id\":\"git-step\",\"snippet_id\":\"" + sid + "\",\"config_overrides\":{\"operation\":\"write_file\",\"repository_id\":\"" + Guid.NewGuid() + "\",\"path\":\"ci/deploy.sh\"}}]");

        var result = await v.ValidateAsync(after, default, previousNodes: before);

        Assert.False(result.IsValid);
    }

    // A JSON null operation is "not set yet", like a missing key.
    [Fact]
    public async Task ANullOperationIsNotAWrite()
    {
        var (v, sid) = Setup(nameof(ANullOperationIsNotAWrite), new[] { "operator" }, holdsGitManage: false);
        var nodes = TestJson.Element(
            "[{\"id\":\"git-step\",\"snippet_id\":\"" + sid + "\",\"config_overrides\":{\"operation\":null}}]");

        Assert.True((await v.ValidateAsync(nodes, default)).IsValid);
    }

    private sealed class ScriptedEffective(bool holdsGitManage) : IEffectivePermissions
    {
        public Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
            => HasAsync(capability, ct);
        public Task<bool> HasAsync(string capability, CancellationToken ct = default)
            => Task.FromResult(capability == "git.manage" ? holdsGitManage : true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class ModeSettings(string mode) : IAppSettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = mode });
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }
}
