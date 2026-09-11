using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GitRepoModel = flow_weaver_backend.Models.GitRepository;

namespace flow_weaver_backend.Tests;

// The public, unauthenticated git-webhook entry point. Every refusal must be
// audited (an admin needs to see WHY a push didn't fire) and no unverified
// delivery may reach the executor. The default posture is deny: no secret and
// no explicit opt-in means rejected.
public class GitWebhookReceiverTests
{
    private const string Secret = "s3cret-signing-key";

    private sealed class RecordingExecutor : IWorkflowExecutor
    {
        public List<(Guid WorkflowId, string? Trigger, JsonElement Input)> Enqueued { get; } = new();
        public Exception? Throw { get; set; }

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId, RunWorkflowRequest request,
            CancellationToken ct, string trigger = "manual")
        {
            if (Throw is not null) throw Throw;
            Enqueued.Add((workflowId, trigger, request.Input));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotSupportedException();
    }

    // Only PullAsync is on the receiver's path; the rest of the (large)
    // IGitService surface throws so an accidental call is loud.
    private sealed class RecordingGitService : IGitService
    {
        public List<(Guid RepoId, string? Branch)> Pulls { get; } = new();
        public Exception? Throw { get; set; }

        public Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct)
        {
            Pulls.Add((id, branch));
            if (Throw is not null) throw Throw;
            return Task.FromResult<ActionResult<GitOpResult>>(new GitOpResult());
        }

        public Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(int limit, int offset, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> CreateAsync(CreateGitRepository dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitListFilesResponse>> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitReadFileResponse>> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ActionResult<GitDiffResponse>> DiffAsync(Guid id, string? from, string? to, string? path, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public RecordingExecutor Executor { get; } = new();
        public RecordingGitService Git { get; } = new();
        public ServiceProvider Sp { get; }
        private readonly string _dbName = Guid.NewGuid().ToString();

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddScoped<IGitWebhookRepository, GitWebhookRepository>();
            services.AddScoped<IJobRepository, JobRepository>();
            services.AddSingleton<ICredentialEncryptionService, FakeCrypto>();
            services.AddSingleton<IWorkflowExecutor>(Executor);
            services.AddSingleton<IGitService>(Git);
            services.AddHttpContextAccessor();
            services.AddScoped<MutableCurrentUser>();
            services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<MutableCurrentUser>());
            services.AddLogging();
            Sp = services.BuildServiceProvider();
        }

        public GitWebhookReceiver Build() => new(
            Sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GitWebhookReceiver>.Instance);

        public AppDbContext NewDb() => Sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public Guid SeedHook(
            string provider = GitWebhook.ProviderGithub,
            bool enabled = true,
            bool withSecret = true,
            bool allowUnsigned = false,
            bool autoPull = false,
            Guid? onPushWorkflowId = null,
            List<string>? branches = null,
            bool active = true)
        {
            using var scope = Sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repoId = Guid.NewGuid();
            db.Set<GitRepoModel>().Add(new GitRepoModel
            {
                GitRepositoryId = repoId,
                Name = "infra",
                Url = "git@github.com:acme/infra.git",
                IsActive = true,
            });
            var id = Guid.NewGuid();
            db.GitWebhooks.Add(new GitWebhook
            {
                GitWebhookId = id,
                GitRepositoryId = repoId,
                Name = "hook",
                Provider = provider,
                Enabled = enabled,
                AllowUnsigned = allowUnsigned,
                AutoPull = autoPull,
                OnPushWorkflowId = onPushWorkflowId,
                OnPushBranches = branches ?? new List<string>(),
                EncryptedSecret = withSecret ? Encoding.UTF8.GetBytes(Secret) : null,
                IsActive = active,
            });
            db.SaveChanges();
            return id;
        }

        public void SeedJobs(int count)
        {
            using var scope = Sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < count; i++)
            {
                db.Jobs.Add(new Job
                {
                    JobId = Guid.NewGuid(),
                    Type = "step",
                    Status = JobStatus.Pending,
                    Tag = "worker",
                    IsActive = true,
                });
            }
            db.SaveChanges();
        }

        public void Dispose() => Sp.Dispose();
    }

    private static byte[] PushBody(string branch = "main", string sha = "abc123")
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            @ref = $"refs/heads/{branch}",
            after = sha,
            head_commit = new { id = sha },
            checkout_sha = sha,
        }));

    private static string GithubSignature(byte[] body)
        => "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();

    private static async Task<GitWebhookDelivery?> LastDelivery(Fixture f)
    {
        using var db = f.NewDb();
        return await db.GitWebhookDeliveries.OrderByDescending(d => d.At).FirstOrDefaultAsync();
    }

    // ─── hook resolution ────────────────────────────────────────────────

    [Fact]
    public async Task UnknownWebhook_Is404()
    {
        using var f = new Fixture();

        var outcome = await f.Build().ReceiveAsync(Guid.NewGuid(), "push", null, PushBody(), default);

        Assert.Equal(404, outcome.StatusCode);
        Assert.Empty(f.Executor.Enqueued);
    }

    [Fact]
    public async Task SoftDeletedWebhook_Is404()
    {
        using var f = new Fixture();
        var id = f.SeedHook(active: false);

        Assert.Equal(404, (await f.Build().ReceiveAsync(id, "push", null, PushBody(), default)).StatusCode);
    }

    // A disabled hook is refused, and the refusal is audited so the admin can
    // see the push arrived.
    [Fact]
    public async Task DisabledWebhook_Is403AndAudited()
    {
        using var f = new Fixture();
        var id = f.SeedHook(enabled: false);

        var outcome = await f.Build().ReceiveAsync(id, "push", null, PushBody(), default);

        Assert.Equal(403, outcome.StatusCode);
        var delivery = await LastDelivery(f);
        Assert.Equal("rejected", delivery!.Status);
        Assert.Contains("disabled", delivery.Error);
    }

    // ─── signature verification ─────────────────────────────────────────

    [Fact]
    public async Task ValidGithubSignatureIsAccepted()
    {
        using var f = new Fixture();
        var workflowId = Guid.NewGuid();
        var id = f.SeedHook(onPushWorkflowId: workflowId);
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.NotNull(outcome.WorkflowRunId);
    }

    [Fact]
    public async Task InvalidSignature_Is401AndAudited()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());

        var outcome = await f.Build().ReceiveAsync(id, "push", "sha256=deadbeef", PushBody(), default);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Empty(f.Executor.Enqueued);
        var delivery = await LastDelivery(f);
        Assert.Equal("rejected", delivery!.Status);
        Assert.Contains("signature mismatch", delivery.Error);
    }

    [Fact]
    public async Task MissingSignatureWhenASecretIsConfigured_Is401()
    {
        using var f = new Fixture();
        var id = f.SeedHook();

        Assert.Equal(401, (await f.Build().ReceiveAsync(id, "push", null, PushBody(), default)).StatusCode);
    }

    // Default-deny: a hook with no secret is refused unless the admin
    // explicitly opted into unsigned deliveries.
    [Fact]
    public async Task NoSecretAndNoOptIn_Is401()
    {
        using var f = new Fixture();
        var id = f.SeedHook(withSecret: false, allowUnsigned: false);

        var outcome = await f.Build().ReceiveAsync(id, "push", null, PushBody(), default);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Contains("allow_unsigned", outcome.Message);
        Assert.Equal("rejected", (await LastDelivery(f))!.Status);
    }

    [Fact]
    public async Task NoSecretWithExplicitOptIn_IsAccepted()
    {
        using var f = new Fixture();
        var id = f.SeedHook(withSecret: false, allowUnsigned: true, onPushWorkflowId: Guid.NewGuid());

        var outcome = await f.Build().ReceiveAsync(id, "push", null, PushBody(), default);

        Assert.Equal(202, outcome.StatusCode);
    }

    // An unknown provider can't be verified, so it must fail closed.
    [Fact]
    public async Task UnknownProviderWithASecretFailsClosed()
    {
        using var f = new Fixture();
        var id = f.SeedHook(provider: "bitbucket");

        Assert.Equal(401, (await f.Build().ReceiveAsync(id, "push", "anything", PushBody(), default)).StatusCode);
    }

    // ─── event routing ──────────────────────────────────────────────────

    // GitHub sends `ping` on hook creation; it must 200 without running work.
    [Fact]
    public async Task NonPushEventIsAcknowledgedWithoutDispatch()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "ping", GithubSignature(body), body, default);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Contains("acknowledged", outcome.Message);
        Assert.Empty(f.Executor.Enqueued);
        Assert.Equal("verified", (await LastDelivery(f))!.Status);
    }

    // ─── branch filtering ───────────────────────────────────────────────

    [Fact]
    public async Task BranchNotInTheFilterIsSkipped()
    {
        using var f = new Fixture();
        var id = f.SeedHook(
            onPushWorkflowId: Guid.NewGuid(), branches: new List<string> { "main" });
        var body = PushBody(branch: "feature/x");

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Contains("filtered out", outcome.Message);
        Assert.Empty(f.Executor.Enqueued);
        var delivery = await LastDelivery(f);
        Assert.Equal("verified", delivery!.Status);
        Assert.Contains("not in filter", delivery.Error);
    }

    [Fact]
    public async Task BranchInTheFilterDispatches()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid(), branches: new List<string> { "main" });
        var body = PushBody(branch: "main");

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Single(f.Executor.Enqueued);
    }

    // An empty filter means "every branch".
    [Fact]
    public async Task EmptyBranchFilterAcceptsAnyBranch()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid(), branches: new List<string>());
        var body = PushBody(branch: "some/other/branch");

        Assert.Equal(202, (await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default)).StatusCode);
    }

    // ─── backpressure ───────────────────────────────────────────────────

    // Once the queue is deep the system is already behind; shed load and let
    // the provider retry.
    [Fact]
    public async Task FullQueue_Is503AndAudited()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        f.SeedJobs(500);
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Empty(f.Executor.Enqueued);
        var delivery = await LastDelivery(f);
        Assert.Equal("rejected", delivery!.Status);
        Assert.Contains("queue full", delivery.Error);
    }

    [Fact]
    public async Task BelowTheQueueThresholdDispatches()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        f.SeedJobs(499);
        var body = PushBody();

        Assert.Equal(202, (await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default)).StatusCode);
    }

    // ─── auto-pull ──────────────────────────────────────────────────────

    [Fact]
    public async Task AutoPullPullsTheRepositoryOnThePushedBranch()
    {
        using var f = new Fixture();
        var id = f.SeedHook(autoPull: true, onPushWorkflowId: Guid.NewGuid());
        var body = PushBody(branch: "release");

        await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal("release", Assert.Single(f.Git.Pulls).Branch);
    }

    // A failed pull must not block the run — an admin may want the run to fire
    // precisely to get visibility on the broken repo.
    [Fact]
    public async Task FailedPullDoesNotBlockTheRun()
    {
        using var f = new Fixture();
        var id = f.SeedHook(autoPull: true, onPushWorkflowId: Guid.NewGuid());
        f.Git.Throw = new InvalidOperationException("git auth failed");
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Single(f.Executor.Enqueued);
    }

    [Fact]
    public async Task AutoPullDisabledSkipsTheRepository()
    {
        using var f = new Fixture();
        var id = f.SeedHook(autoPull: false, onPushWorkflowId: Guid.NewGuid());
        var body = PushBody();

        await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Empty(f.Git.Pulls);
    }

    // ─── dispatch ───────────────────────────────────────────────────────

    // The run input carries the push context so the workflow can act on the
    // commit that triggered it.
    [Fact]
    public async Task RunInputCarriesThePushContext()
    {
        using var f = new Fixture();
        var workflowId = Guid.NewGuid();
        var id = f.SeedHook(onPushWorkflowId: workflowId);
        var body = PushBody(branch: "main", sha: "cafebabe");

        await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        var call = Assert.Single(f.Executor.Enqueued);
        Assert.Equal(workflowId, call.WorkflowId);
        Assert.Equal("webhook", call.Trigger);
        Assert.Equal("main", call.Input.GetProperty("branch").GetString());
        Assert.Equal("cafebabe", call.Input.GetProperty("commit_sha").GetString());
        Assert.Equal(id, call.Input.GetProperty("git_webhook_id").GetGuid());
    }

    // A verified push against a hook with no workflow is still a success — the
    // admin may only want auto-pull.
    [Fact]
    public async Task VerifiedPushWithoutAWorkflowIsStillAccepted()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: null);
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Contains("no workflow configured", outcome.Message);
        Assert.Null(outcome.WorkflowRunId);
        Assert.Equal("dispatched", (await LastDelivery(f))!.Status);
    }

    // An enqueue failure is reported as a 500 and recorded with the reason.
    [Fact]
    public async Task EnqueueFailure_Is500AndRecordsTheError()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        f.Executor.Throw = new InvalidOperationException("workflow is in needs_config");
        var body = PushBody();

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        Assert.Equal(500, outcome.StatusCode);
        Assert.Contains("needs_config", outcome.Message);
        var delivery = await LastDelivery(f);
        Assert.Equal("failed", delivery!.Status);
        Assert.Contains("needs_config", delivery.Error);
    }

    // ─── delivery audit trail ───────────────────────────────────────────

    [Fact]
    public async Task SuccessfulDeliveryIsRecordedWithTheCommitDetails()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        var body = PushBody(branch: "main", sha: "abc123");

        var outcome = await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        var delivery = await LastDelivery(f);
        Assert.Equal("dispatched", delivery!.Status);
        Assert.Equal("main", delivery.Branch);
        Assert.Equal("abc123", delivery.CommitSha);
        Assert.Equal(outcome.WorkflowRunId, delivery.WorkflowRunId);
        Assert.Null(delivery.Error);
    }

    // The parent hook is stamped so the UI shows "last delivery" without a join.
    [Fact]
    public async Task ParentHookIsStampedWithTheLastDeliveryStatus()
    {
        using var f = new Fixture();
        var id = f.SeedHook(onPushWorkflowId: Guid.NewGuid());
        var body = PushBody();

        await f.Build().ReceiveAsync(id, "push", GithubSignature(body), body, default);

        using var db = f.NewDb();
        var hook = await db.GitWebhooks.SingleAsync();
        Assert.Equal("dispatched", hook.LastDeliveryStatus);
        Assert.NotNull(hook.LastDeliveryAt);
    }
}
