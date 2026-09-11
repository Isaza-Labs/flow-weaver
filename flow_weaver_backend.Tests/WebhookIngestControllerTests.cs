using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GitRepoModel = flow_weaver_backend.Models.GitRepository;
using WorkflowTriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Tests;

// The two public, unauthenticated webhook ingest endpoints. They share a shape:
// buffer the body, cap it, hand it to a receiver, and write an audit row for
// EVERY hit — accepted or refused. The audit row is the part that matters for
// operations: an admin looking at /admin/audit has to be able to see that a
// push arrived and why it did or didn't fire anything.
public class WebhookIngestControllerTests
{
    private const string Secret = "s3cret-signing-key";

    private sealed class RecordingExecutor : IWorkflowExecutor
    {
        public List<(Guid WorkflowId, string? Trigger, JsonElement Input, List<Guid> Devices)> Enqueued { get; } = new();
        public Exception? Throw { get; set; }

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId, RunWorkflowRequest request,
            CancellationToken ct, string trigger = "manual")
        {
            if (Throw is not null) throw Throw;
            Enqueued.Add((workflowId, trigger, request.Input, request.TargetDevices));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task ExecuteRunAsync(Guid runId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotSupportedException();
    }

    private sealed class StubGitService : IGitService
    {
        public List<Guid> Pulls { get; } = new();
        public Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct)
        {
            Pulls.Add(id);
            return Task.FromResult<ActionResult<GitOpResult>>(new GitOpResult());
        }
        // Everything else is unreachable from the webhook path under test.
        public Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(int limit, int offset, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> CreateAsync(CreateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitListFilesResponse>> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitReadFileResponse>> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitOpResult>> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult<GitDiffResponse>> DiffAsync(Guid id, string? @from, string? to, string? path, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dbName = Guid.NewGuid().ToString();
        public ServiceProvider Sp { get; }
        public RecordingExecutor Executor { get; } = new();
        public StubGitService Git { get; } = new();
        public DefaultHttpContext HttpContext { get; } = new();

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddScoped<IGitWebhookRepository, GitWebhookRepository>();
            services.AddScoped<IWorkflowTriggerRepository, WorkflowTriggerRepository>();
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

        public AppDbContext NewDb() => Sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public GitWebhookIngestController BuildGit()
        {
            var controller = new GitWebhookIngestController(
                new GitWebhookReceiver(
                    Sp.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<GitWebhookReceiver>.Instance),
                NewDb(),
                NullLogger<GitWebhookIngestController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = HttpContext };
            return controller;
        }

        public WorkflowWebhookController BuildWorkflow()
        {
            var controller = new WorkflowWebhookController(
                new WorkflowWebhookReceiver(
                    Sp.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<WorkflowWebhookReceiver>.Instance),
                NewDb(),
                NullLogger<WorkflowWebhookController>.Instance);
            controller.ControllerContext = new ControllerContext { HttpContext = HttpContext };
            return controller;
        }

        public void SetBody(byte[] body)
        {
            HttpContext.Request.Body = new MemoryStream(body);
            HttpContext.Request.ContentLength = body.Length;
        }

        public void SetBody(string body) => SetBody(Encoding.UTF8.GetBytes(body));

        public void Dispose() => Sp.Dispose();
    }

    private static string Sign(byte[] body)
        => "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();

    // Reads the anonymous body the ingest endpoints return.
    private static (int Status, bool Ok, Guid? RunId) Read(IActionResult result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        var value = obj.Value!;
        return (obj.StatusCode ?? 0,
                (bool)value.GetType().GetProperty("ok")!.GetValue(value)!,
                (Guid?)value.GetType().GetProperty("workflow_run_id")!.GetValue(value));
    }

    private static AuditEvent SingleAudit(Fixture f)
        => Assert.Single(f.NewDb().AuditLogs.ToList());

    // ─── Git webhook ingest ─────────────────────────────────────────────

    private static Guid SeedGitHook(
        Fixture f,
        bool enabled = true,
        bool withSecret = true,
        bool allowUnsigned = false,
        Guid? onPushWorkflowId = null,
        bool autoPull = false)
    {
        using var db = f.NewDb();
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
            Provider = GitWebhook.ProviderGithub,
            Enabled = enabled,
            AllowUnsigned = allowUnsigned,
            EncryptedSecret = withSecret ? Encoding.UTF8.GetBytes(Secret) : null,
            OnPushWorkflowId = onPushWorkflowId,
            AutoPull = autoPull,
            IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task GitIngest_AnUnknownWebhookIs404AndStillAudited()
    {
        using var f = new Fixture();
        f.SetBody("{}");
        var id = Guid.NewGuid();

        var result = await f.BuildGit().Ingest(id, default);

        Assert.Equal(404, Read(result).Status);
        var audit = SingleAudit(f);
        Assert.Equal("git_webhook.ingest.unknown", audit.Action);
        // An unknown hook still leaves an audit row that is searchable by
        // webhook id.
        Assert.Equal(id, audit.EntityId);
    }

    [Fact]
    public async Task GitIngest_AnOversizedPayloadIs413BeforeTheReceiverRuns()
    {
        using var f = new Fixture();
        var id = SeedGitHook(f);
        f.SetBody(new byte[1024 * 1024 + 1]);

        var result = await f.BuildGit().Ingest(id, default);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result).StatusCode);
        // Short-circuits before the audit write, since the receiver never ran.
        Assert.Empty(f.NewDb().AuditLogs.ToList());
    }

    // An unsigned hit on a hook that has a secret is a rejection, and the
    // audit row has to say so — that is how an admin spots a misconfigured
    // sender versus an attack.
    [Fact]
    public async Task GitIngest_AnUnsignedHitOnASecretHookIsRejectedAndAudited()
    {
        using var f = new Fixture();
        var id = SeedGitHook(f);
        f.SetBody("""{"ref":"refs/heads/main"}""");
        f.HttpContext.Request.Headers["X-GitHub-Event"] = "push";

        var result = await f.BuildGit().Ingest(id, default);

        Assert.Equal(401, Read(result).Status);
        var audit = SingleAudit(f);
        Assert.Equal("git_webhook.ingest.rejected", audit.Action);
        Assert.False(audit.AfterJson.GetProperty("signed").GetBoolean());
        Assert.Equal("push", audit.AfterJson.GetProperty("event_name").GetString());
    }

    [Fact]
    public async Task GitIngest_AValidSignedPushFiresTheWorkflowAndAuditsOk()
    {
        using var f = new Fixture();
        var workflowId = Guid.NewGuid();
        var id = SeedGitHook(f, onPushWorkflowId: workflowId);
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-GitHub-Event"] = "push";
        f.HttpContext.Request.Headers["X-Hub-Signature-256"] = Sign(body);

        var result = await f.BuildGit().Ingest(id, default);

        var read = Read(result);
        Assert.True(read.Ok);
        Assert.NotNull(read.RunId);
        Assert.Equal(workflowId, Assert.Single(f.Executor.Enqueued).WorkflowId);

        var audit = SingleAudit(f);
        Assert.Equal("git_webhook.ingest.ok", audit.Action);
        Assert.True(audit.AfterJson.GetProperty("signed").GetBoolean());
        Assert.Equal(body.Length, audit.AfterJson.GetProperty("body_bytes").GetInt32());
    }

    // GitLab senders use a different header pair; both must be read.
    [Fact]
    public async Task GitIngest_ReadsTheGitlabHeaderPair()
    {
        using var f = new Fixture();
        var id = SeedGitHook(f);
        f.SetBody("{}");
        f.HttpContext.Request.Headers["X-Gitlab-Event"] = "Push Hook";
        f.HttpContext.Request.Headers["X-Gitlab-Token"] = "wrong-token";

        var result = await f.BuildGit().Ingest(id, default);

        var audit = SingleAudit(f);
        Assert.Equal("Push Hook", audit.AfterJson.GetProperty("event_name").GetString());
        // A token WAS supplied, so the hit counts as signed even though it
        // didn't verify.
        Assert.True(audit.AfterJson.GetProperty("signed").GetBoolean());
        Assert.Equal(401, Read(result).Status);
    }

    // A disabled hook is a deliberate off switch, not an error.
    [Fact]
    public async Task GitIngest_ADisabledHookIsRejected()
    {
        using var f = new Fixture();
        var id = SeedGitHook(f, enabled: false);
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-Hub-Signature-256"] = Sign(body);

        var result = await f.BuildGit().Ingest(id, default);

        Assert.False(Read(result).Ok);
        Assert.Equal("git_webhook.ingest.rejected", SingleAudit(f).Action);
    }

    // The audit row carries the repo + provider so a hit can be traced back
    // to what it was pointed at.
    [Fact]
    public async Task GitIngest_TheAuditRowIdentifiesTheRepositoryAndProvider()
    {
        using var f = new Fixture();
        var id = SeedGitHook(f);
        f.SetBody("{}");

        await f.BuildGit().Ingest(id, default);

        var audit = SingleAudit(f);
        Assert.Equal("github", audit.AfterJson.GetProperty("provider").GetString());
        Assert.NotEqual(Guid.Empty, audit.AfterJson.GetProperty("repo_id").GetGuid());
        Assert.Equal("GitWebhook", audit.EntityType);
    }

    // ─── Workflow-trigger webhook ingest ────────────────────────────────

    private static Guid SeedTrigger(
        Fixture f,
        bool enabled = true,
        bool withSecret = true,
        bool allowUnsigned = false,
        string type = WorkflowTriggerModel.TypeWebhook,
        Guid? workflowId = null,
        string? inputDefaults = null,
        List<Guid>? targetDevices = null,
        bool allowTargetOverride = false)
    {
        using var db = f.NewDb();
        var id = Guid.NewGuid();
        db.WorkflowTriggers.Add(new WorkflowTriggerModel
        {
            WorkflowTriggerId = id,
            WorkflowId = workflowId ?? Guid.NewGuid(),
            Name = "ci-hook",
            Type = type,
            Enabled = enabled,
            AllowUnsigned = allowUnsigned,
            AllowTargetOverride = allowTargetOverride,
            EncryptedSecret = withSecret ? Encoding.UTF8.GetBytes(Secret) : null,
            InputDefaults = TestJson.Element(inputDefaults ?? "{}"),
            TargetDevices = targetDevices ?? new List<Guid>(),
            IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task WorkflowIngest_AnUnknownTriggerIs404AndAudited()
    {
        using var f = new Fixture();
        f.SetBody("{}");
        var id = Guid.NewGuid();

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(404, Read(result).Status);
        var audit = SingleAudit(f);
        Assert.Equal("workflow_webhook.ingest.unknown", audit.Action);
        Assert.Equal("WorkflowTrigger", audit.EntityType);
    }

    // A cron trigger's id must never be firable from the public endpoint —
    // and it reports as "unknown" rather than confirming the id exists.
    [Fact]
    public async Task WorkflowIngest_ANonWebhookTriggerIsNotIngestable()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f, type: "schedule");
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(404, Read(result).Status);
        Assert.Empty(f.Executor.Enqueued);
    }

    [Fact]
    public async Task WorkflowIngest_ASignedHitEnqueuesTheRun()
    {
        using var f = new Fixture();
        var workflowId = Guid.NewGuid();
        var id = SeedTrigger(f, workflowId: workflowId);
        var body = Encoding.UTF8.GetBytes("""{"branch":"main"}""");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        var result = await f.BuildWorkflow().Ingest(id, default);

        var read = Read(result);
        Assert.Equal(202, read.Status);
        Assert.NotNull(read.RunId);

        var enqueued = Assert.Single(f.Executor.Enqueued);
        Assert.Equal(workflowId, enqueued.WorkflowId);
        Assert.Equal("webhook", enqueued.Trigger);
        // The body rides under `webhook`, with the trigger id as provenance.
        Assert.Equal("main", enqueued.Input.GetProperty("webhook").GetProperty("branch").GetString());
        Assert.Equal(id.ToString(), enqueued.Input.GetProperty("trigger_id").GetString());

        Assert.Equal("workflow_webhook.ingest.ok", SingleAudit(f).Action);
    }

    // Default posture is deny: no secret and no explicit opt-in is a refusal.
    [Fact]
    public async Task WorkflowIngest_ATriggerWithNoSecretIsRejectedByDefault()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f, withSecret: false);
        f.SetBody("{}");

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(401, Read(result).Status);
        Assert.Empty(f.Executor.Enqueued);
        Assert.Equal("workflow_webhook.ingest.rejected", SingleAudit(f).Action);
    }

    // allow_unsigned is the admin's explicit opt-out of signing.
    [Fact]
    public async Task WorkflowIngest_AllowUnsignedLetsAnUnsignedHitThrough()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f, withSecret: false, allowUnsigned: true);
        f.SetBody("{}");

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(202, Read(result).Status);
        Assert.Single(f.Executor.Enqueued);
    }

    [Fact]
    public async Task WorkflowIngest_ABadSignatureIsRejected()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        f.SetBody("""{"branch":"main"}""");
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = "sha256=deadbeef";

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(401, Read(result).Status);
        Assert.Empty(f.Executor.Enqueued);
    }

    [Fact]
    public async Task WorkflowIngest_ADisabledTriggerIs403()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f, enabled: false);
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(403, Read(result).Status);
        Assert.Equal("workflow_webhook.ingest.rejected", SingleAudit(f).Action);
    }

    // Body-supplied targets are refused unless the trigger opts in: the caller
    // authenticates with the shared secret and the run skips the env/resource
    // RBAC a manual run passes, so the body must not widen the blast radius.
    [Fact]
    public async Task WorkflowIngest_BodySuppliedTargetsAreIgnoredByDefault()
    {
        using var f = new Fixture();
        var configured = Guid.NewGuid();
        var fromBody = Guid.NewGuid();
        var id = SeedTrigger(f, targetDevices: new List<Guid> { configured });
        var body = Encoding.UTF8.GetBytes(
            "{\"target_devices\":[\"" + fromBody + "\"]}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(configured, Assert.Single(Assert.Single(f.Executor.Enqueued).Devices));
    }

    // With allow_target_override on an unscoped trigger the caller picks, which
    // is the per-call targeting the endpoint was built for.
    [Fact]
    public async Task WorkflowIngest_BodySuppliedTargetsApplyWhenTheTriggerOptsIn()
    {
        using var f = new Fixture();
        var fromBody = Guid.NewGuid();
        var id = SeedTrigger(f, allowTargetOverride: true);
        var body = Encoding.UTF8.GetBytes(
            "{\"target_devices\":[\"" + fromBody + "\"]}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(fromBody, Assert.Single(Assert.Single(f.Executor.Enqueued).Devices));
    }

    [Fact]
    public async Task WorkflowIngest_TheTriggersTargetsAreUsedWhenTheBodyHasNone()
    {
        using var f = new Fixture();
        var configured = Guid.NewGuid();
        var id = SeedTrigger(f, targetDevices: new List<Guid> { configured });
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(configured, Assert.Single(Assert.Single(f.Executor.Enqueued).Devices));
    }

    // The trigger's configured defaults are merged in, but the payload and
    // provenance keys can't be shadowed by them.
    [Fact]
    public async Task WorkflowIngest_InputDefaultsAreMergedUnderThePayload()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f, inputDefaults: """{"site":"madrid","webhook":"should-be-overwritten"}""");
        var body = Encoding.UTF8.GetBytes("""{"branch":"main"}""");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        await f.BuildWorkflow().Ingest(id, default);

        var input = Assert.Single(f.Executor.Enqueued).Input;
        Assert.Equal("madrid", input.GetProperty("site").GetString());
        Assert.Equal("main", input.GetProperty("webhook").GetProperty("branch").GetString());
    }

    // A non-JSON body still fires the workflow — with just the defaults and
    // provenance, and `webhook` explicitly null so the workflow can tell.
    [Fact]
    public async Task WorkflowIngest_ANonJsonBodyStillFiresWithNullPayload()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        var body = Encoding.UTF8.GetBytes("not json at all");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(202, Read(result).Status);
        Assert.Equal(JsonValueKind.Null,
            Assert.Single(f.Executor.Enqueued).Input.GetProperty("webhook").ValueKind);
    }

    // The executor's pre-flight failures (env mismatch, missing creds) surface
    // as a 500 so the sender knows to retry, and the trigger records it.
    [Fact]
    public async Task WorkflowIngest_AnExecutorFailureIs500AndStampsTheTrigger()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        f.Executor.Throw = new WorkflowExecutorException("workflow environment 'production' requires ...");
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(500, Read(result).Status);
        Assert.Equal("workflow_webhook.ingest.error", SingleAudit(f).Action);
        using var db = f.NewDb();
        Assert.Equal("webhook_failed", db.WorkflowTriggers.Single().LastRunStatus);
    }

    // Every accepted hit stamps the trigger so the UI can show "last fired".
    [Fact]
    public async Task WorkflowIngest_ASuccessStampsTheTrigger()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        var body = Encoding.UTF8.GetBytes("{}");
        f.SetBody(body);
        f.HttpContext.Request.Headers["X-FlowWeaver-Signature"] = Sign(body);

        await f.BuildWorkflow().Ingest(id, default);

        using var db = f.NewDb();
        var trigger = db.WorkflowTriggers.Single();
        Assert.Equal("webhook_dispatched", trigger.LastRunStatus);
        Assert.NotNull(trigger.LastRunAt);
    }

    [Fact]
    public async Task WorkflowIngest_AnOversizedPayloadIs413()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        f.SetBody(new byte[1024 * 1024 + 1]);

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(f.Executor.Enqueued);
    }

    // The shared-secret token header is an alternative to the HMAC signature.
    [Fact]
    public async Task WorkflowIngest_TheSharedSecretTokenHeaderIsAcceptedToo()
    {
        using var f = new Fixture();
        var id = SeedTrigger(f);
        f.SetBody("{}");
        f.HttpContext.Request.Headers["X-FlowWeaver-Token"] = Secret;

        var result = await f.BuildWorkflow().Ingest(id, default);

        Assert.Equal(202, Read(result).Status);
        Assert.True(SingleAudit(f).AfterJson.GetProperty("signed").GetBoolean());
    }
}
