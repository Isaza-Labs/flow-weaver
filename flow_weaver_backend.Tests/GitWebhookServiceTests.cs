using System.Text;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using GitRepoModel = flow_weaver_backend.Models.GitRepository;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// CRUD for the webhooks that let a git push trigger a workflow. The details
// that matter: the signing secret is write-only (never echoed back, only a
// has_secret flag), a soft delete keeps the row, and the partial-update
// semantics distinguish "not supplied" from "clear it".
public class GitWebhookServiceTests
{

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public IHttpContextAccessor Http { get; set; } = new HttpContextAccessor();

        public GitWebhookService Build() => new(
            new GitWebhookRepository(Db),
            new RepositoryBase<GitRepoModel>(Db),
            new RepositoryBase<WorkflowModel>(Db),
            new FakeUser(),
            new FakeCrypto(),
            Http,
            NullLogger<GitWebhookService>.Instance);

        public Guid SeedRepo(bool active = true)
        {
            var id = Guid.NewGuid();
            Db.Set<GitRepoModel>().Add(new GitRepoModel
            {
                GitRepositoryId = id,
                Name = "infra",
                Url = "git@github.com:acme/infra.git",
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedWorkflow()
        {
            var id = Guid.NewGuid();
            Db.Set<WorkflowModel>().Add(new WorkflowModel
            {
                WorkflowId = id,
                Name = "deploy",
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid SeedWebhook(
            Guid repoId, string provider = GitWebhook.ProviderGithub,
            string? secret = "s3cret", bool active = true)
        {
            var id = Guid.NewGuid();
            Db.GitWebhooks.Add(new GitWebhook
            {
                GitWebhookId = id,
                GitRepositoryId = repoId,
                Name = "push-hook",
                Provider = provider,
                EncryptedSecret = secret is null ? null : Encoding.UTF8.GetBytes(secret),
                OnPushBranches = new List<string> { "main" },
                AutoPull = true,
                Enabled = true,
                IsActive = active,
            });
            Db.SaveChanges();
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static T Value<T>(ActionResult<T> result) where T : class
    {
        Assert.Null(result.Result);
        return Assert.IsType<T>(result.Value);
    }

    private static int StatusOf<T>(ActionResult<T> result)
        => Assert.IsType<ObjectResult>(result.Result).StatusCode!.Value;

    private static CreateGitWebhook CreateDto(
        string name = "push-hook", string provider = "github",
        string? secret = "s3cret", Guid? workflowId = null,
        List<string>? branches = null)
        => new()
        {
            Name = name,
            Provider = provider,
            Secret = secret,
            OnPushWorkflowId = workflowId,
            OnPushBranches = branches ?? new List<string> { "main" },
        };

    // ─── create ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsAndReturns201()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(), default);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        var body = Assert.IsType<GitWebhookResponse>(created.Value);
        Assert.Equal("push-hook", body.Name);
        Assert.Equal("github", body.Provider);
        Assert.Equal(repoId, body.GitRepositoryId);
        Assert.Single(f.Db.GitWebhooks);
    }

    [Fact]
    public async Task Create_UnknownRepository_Is404()
    {
        using var f = new Fixture();

        var result = await f.Build().CreateAsync(Guid.NewGuid(), CreateDto(), default);

        Assert.Equal(404, StatusOf(result));
        Assert.Empty(f.Db.GitWebhooks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_MissingName_Is400(string name)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(400, StatusOf(await f.Build().CreateAsync(repoId, CreateDto(name: name), default)));
    }

    [Theory]
    [InlineData("bitbucket")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_InvalidProvider_Is400(string provider)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(400, StatusOf(await f.Build().CreateAsync(repoId, CreateDto(provider: provider), default)));
    }

    [Theory]
    [InlineData("GitHub", "github")]
    [InlineData("  GITLAB  ", "gitlab")]
    [InlineData("Generic", "generic")]
    public async Task Create_ProviderIsNormalised(string input, string expected)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(provider: input), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal(expected, body.Provider);
    }

    // A hook pointing at a workflow that doesn't exist would fail silently at
    // push time, so it is rejected up front.
    [Fact]
    public async Task Create_UnknownWorkflow_Is400()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(workflowId: Guid.NewGuid()), default);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Create_KnownWorkflowIsAccepted()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var workflowId = f.SeedWorkflow();

        var result = await f.Build().CreateAsync(repoId, CreateDto(workflowId: workflowId), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal(workflowId, body.OnPushWorkflowId);
    }

    // The secret is stored encrypted and surfaced only as a boolean — echoing it
    // back would leak it to anyone who can read the hook.
    [Fact]
    public async Task Create_SecretIsEncryptedAndNeverEchoed()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(secret: "top-secret"), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.True(body.HasSecret);
        var row = await f.Db.GitWebhooks.SingleAsync();
        Assert.Equal("top-secret", Encoding.UTF8.GetString(row.EncryptedSecret!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Create_WithoutSecret_HasSecretIsFalse(string? secret)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(secret: secret), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.False(body.HasSecret);
    }

    // Branch filters are trimmed and de-duplicated so the push matcher gets a
    // clean list.
    [Fact]
    public async Task Create_BranchesAreTrimmedDedupedAndBlanksDropped()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var branches = new List<string> { " main ", "main", "", "   ", "release" };

        var result = await f.Build().CreateAsync(repoId, CreateDto(branches: branches), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "main", "release" }, body.OnPushBranches);
    }

    // The ingestion URL is derived from the live request so the admin can copy
    // it straight into the provider's UI.
    [Fact]
    public async Task Create_IngestionUrlUsesTheRequestOrigin()
    {
        using var f = new Fixture();
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("fw.example.com");
        f.Http = new HttpContextAccessor { HttpContext = ctx };
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.StartsWith("https://fw.example.com/api/git/webhooks/", body.IngestionUrl);
    }

    // Without an HTTP request (background contexts) the URL is simply absent.
    [Fact]
    public async Task Create_WithoutRequest_IngestionUrlIsNull()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        var result = await f.Build().CreateAsync(repoId, CreateDto(), default);

        var body = Assert.IsType<GitWebhookResponse>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Null(body.IngestionUrl);
    }

    // ─── read ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOnlyThisRepositorysHooks()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var otherRepoId = f.SeedRepo();
        f.SeedWebhook(repoId);
        f.SeedWebhook(otherRepoId);

        var body = Value(await f.Build().ListAsync(repoId, default));

        Assert.Single(body.Data);
        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task List_UnknownRepository_Is404()
    {
        using var f = new Fixture();

        Assert.Equal(404, StatusOf(await f.Build().ListAsync(Guid.NewGuid(), default)));
    }

    [Fact]
    public async Task List_ExcludesSoftDeletedHooks()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        f.SeedWebhook(repoId, active: false);

        Assert.Empty(Value(await f.Build().ListAsync(repoId, default)).Data);
    }

    [Fact]
    public async Task Get_ReturnsTheHook()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().GetAsync(repoId, hookId, default));

        Assert.Equal(hookId, body.GitWebhookId);
        Assert.True(body.HasSecret);
    }

    [Fact]
    public async Task Get_UnknownHook_Is404()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(404, StatusOf(await f.Build().GetAsync(repoId, Guid.NewGuid(), default)));
    }

    // A hook must only be reachable through its OWN repository.
    [Fact]
    public async Task Get_ThroughTheWrongRepository_Is404()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var otherRepoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        Assert.Equal(404, StatusOf(await f.Build().GetAsync(otherRepoId, hookId, default)));
    }

    // ─── update (partial semantics) ─────────────────────────────────────

    [Fact]
    public async Task Update_UnknownHook_Is404()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(404, StatusOf(
            await f.Build().UpdateAsync(repoId, Guid.NewGuid(), new UpdateGitWebhook(), default)));
    }

    // An empty patch must leave everything as it was.
    [Fact]
    public async Task Update_EmptyPatchChangesNothing()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook(), default));

        Assert.Equal("push-hook", body.Name);
        Assert.Equal("github", body.Provider);
        Assert.True(body.HasSecret);
        Assert.True(body.Enabled);
    }

    [Fact]
    public async Task Update_ChangesSuppliedFields()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook
        {
            Name = "renamed",
            Provider = "GitLab",
            OnPushBranches = new List<string> { " dev " },
            AutoPull = false,
            Enabled = false,
            AllowUnsigned = true,
        }, default));

        Assert.Equal("renamed", body.Name);
        Assert.Equal("gitlab", body.Provider);
        Assert.Equal(new[] { "dev" }, body.OnPushBranches);
        Assert.False(body.AutoPull);
        Assert.False(body.Enabled);
        Assert.True(body.AllowUnsigned);
    }

    // A blank name is treated as "not supplied" rather than wiping the name.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_BlankNameIsIgnored(string name)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook { Name = name }, default));

        Assert.Equal("push-hook", body.Name);
    }

    [Fact]
    public async Task Update_InvalidProvider_Is400()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        Assert.Equal(400, StatusOf(
            await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook { Provider = "svn" }, default)));
    }

    // The three-state secret contract: null = leave alone, "" = clear,
    // anything else = replace. Getting this wrong either leaks or silently
    // disables signature verification.
    [Fact]
    public async Task Update_NullSecretLeavesItUntouched()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId, secret: "original");

        await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook { Secret = null }, default);

        var row = await f.Db.GitWebhooks.SingleAsync();
        Assert.Equal("original", Encoding.UTF8.GetString(row.EncryptedSecret!));
    }

    [Fact]
    public async Task Update_EmptySecretClearsIt()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId, secret: "original");

        var body = Value(await f.Build().UpdateAsync(
            repoId, hookId, new UpdateGitWebhook { Secret = "" }, default));

        Assert.False(body.HasSecret);
        Assert.Null((await f.Db.GitWebhooks.SingleAsync()).EncryptedSecret);
    }

    [Fact]
    public async Task Update_NewSecretReplacesTheOldOne()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId, secret: "original");

        await f.Build().UpdateAsync(repoId, hookId, new UpdateGitWebhook { Secret = "rotated" }, default);

        Assert.Equal("rotated", Encoding.UTF8.GetString((await f.Db.GitWebhooks.SingleAsync()).EncryptedSecret!));
    }

    // ─── delete ─────────────────────────────────────────────────────────

    // Soft delete: the row survives so its delivery history stays readable.
    [Fact]
    public async Task Delete_IsASoftDelete()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().DeleteAsync(repoId, hookId, default));

        Assert.Equal(hookId, body.GitWebhookId);
        var row = await f.Db.GitWebhooks.SingleAsync();
        Assert.False(row.IsActive);
    }

    [Fact]
    public async Task Delete_UnknownHook_Is404()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(404, StatusOf(await f.Build().DeleteAsync(repoId, Guid.NewGuid(), default)));
    }

    [Fact]
    public async Task Delete_IsIdempotentlyRefusedOnceGone()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);
        var svc = f.Build();
        await svc.DeleteAsync(repoId, hookId, default);

        Assert.Equal(404, StatusOf(await svc.DeleteAsync(repoId, hookId, default)));
    }

    // ─── deliveries ─────────────────────────────────────────────────────

    private static void SeedDelivery(Fixture f, Guid hookId, string status = "ok")
    {
        f.Db.GitWebhookDeliveries.Add(new GitWebhookDelivery
        {
            GitWebhookDeliveryId = Guid.NewGuid(),
            GitWebhookId = hookId,
            Status = status,
            Event = "push",
            Branch = "main",
            CommitSha = "abc1234",
            At = DateTime.UtcNow,
            IsActive = true,
        });
        f.Db.SaveChanges();
    }

    [Fact]
    public async Task ListDeliveries_ReturnsTheAuditTrail()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);
        SeedDelivery(f, hookId);

        var body = Value(await f.Build().ListDeliveriesAsync(repoId, hookId, 50, default));

        var row = Assert.Single(body.Data);
        Assert.Equal("push", row.Event);
        Assert.Equal("main", row.Branch);
        Assert.Equal("abc1234", row.CommitSha);
    }

    [Fact]
    public async Task ListDeliveries_UnknownHook_Is404()
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();

        Assert.Equal(404, StatusOf(
            await f.Build().ListDeliveriesAsync(repoId, Guid.NewGuid(), 50, default)));
    }

    // The limit is clamped so a caller can't ask for an unbounded page.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 200)]
    [InlineData(50, 50)]
    public async Task ListDeliveries_LimitIsClamped(int requested, int expected)
    {
        using var f = new Fixture();
        var repoId = f.SeedRepo();
        var hookId = f.SeedWebhook(repoId);

        var body = Value(await f.Build().ListDeliveriesAsync(repoId, hookId, requested, default));

        Assert.Equal(expected, body.Limit);
    }
}
