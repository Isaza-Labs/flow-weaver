using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// FR-027 / TC-FW-061 — inbound webhook trigger. Exercises the three acceptance
// cases (legit fire, auth rejection, and the SSRF property) plus the guard rails,
// through WorkflowWebhookReceiver end-to-end against InMemory EF + a recording
// executor. Signature verification and the enqueue path are real; the executor
// records the call instead of orchestrating.
public class WorkflowWebhookReceiverTests
{
    private const string Secret = "s3cr3t-signing-key";

    private static ServiceProvider BuildProvider(string dbName, RecordingExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IWorkflowTriggerRepository, WorkflowTriggerRepository>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddSingleton<ICredentialEncryptionService, FakeCrypto>();
        services.AddHttpContextAccessor();
        services.AddScoped<MutableCurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<MutableCurrentUser>());
        services.AddSingleton<IWorkflowExecutor>(executor);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static Guid SeedTrigger(
        ServiceProvider sp, string type = WorkflowTrigger.TypeWebhook,
        bool enabled = true, bool withSecret = true, bool allowUnsigned = false,
        bool allowTargetOverride = false, List<Guid>? targetDevices = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<ICredentialEncryptionService>();
        var id = Guid.NewGuid();
        db.WorkflowTriggers.Add(new WorkflowTrigger
        {
            WorkflowTriggerId = id,
            WorkflowId = Guid.NewGuid(),
            Name = "hook",
            Type = type,
            Enabled = enabled,
            AllowUnsigned = allowUnsigned,
            AllowTargetOverride = allowTargetOverride,
            TargetDevices = targetDevices ?? new List<Guid>(),
            EncryptedSecret = withSecret ? crypto.Encrypt(Secret) : null,
            IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    private static WorkflowWebhookReceiver NewReceiver(ServiceProvider sp, RecordingExecutor executor) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkflowWebhookReceiver>.Instance);

    private static string HmacHeader(byte[] body, string secret)
        => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    // ── (a) legitimate webhook fires the run ──────────────────────────────────

    [Fact]
    public async Task Valid_hmac_signature_enqueues_exactly_one_run()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Valid_hmac_signature_enqueues_exactly_one_run), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("""{"event":"deploy"}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.NotNull(outcome.WorkflowRunId);
        Assert.Equal(1, executor.CallCount);   // exactly one run
    }

    [Fact]
    public async Task Valid_shared_token_enqueues_the_run()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Valid_shared_token_enqueues_the_run), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("{}");

        // Token header carries the raw secret (GitLab-style), no HMAC.
        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, null, Secret, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.True(executor.CallCount == 1);
    }

    // ── (b) auth rejections (burst-limiting is at the controller/rate-limiter) ─

    [Fact]
    public async Task Invalid_signature_is_rejected_401_without_enqueuing()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Invalid_signature_is_rejected_401_without_enqueuing), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("""{"event":"deploy"}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, "sha256=deadbeef", null, body, CancellationToken.None);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);   // no run
    }

    [Fact]
    public async Task Missing_signature_on_a_secured_trigger_is_rejected_401()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Missing_signature_on_a_secured_trigger_is_rejected_401), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, null, null, body, CancellationToken.None);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task No_secret_and_allow_unsigned_accepts_the_delivery()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(No_secret_and_allow_unsigned_accepts_the_delivery), executor);
        var id = SeedTrigger(sp, withSecret: false, allowUnsigned: true);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, null, null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task No_secret_and_not_allow_unsigned_is_rejected_401()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(No_secret_and_not_allow_unsigned_is_rejected_401), executor);
        var id = SeedTrigger(sp, withSecret: false, allowUnsigned: false);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, null, null, body, CancellationToken.None);

        Assert.Equal(401, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    // ── guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_trigger_is_403()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Disabled_trigger_is_403), executor);
        var id = SeedTrigger(sp, enabled: false);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(403, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Unknown_trigger_is_404()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Unknown_trigger_is_404), executor);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(Guid.NewGuid(), null, Secret, body, CancellationToken.None);

        Assert.Equal(404, outcome.StatusCode);
    }

    [Fact]
    public async Task A_non_webhook_trigger_is_not_ingestable_404()
    {
        // A cron trigger's id must never be firable from the public endpoint.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(A_non_webhook_trigger_is_not_ingestable_404), executor);
        var id = SeedTrigger(sp, type: "cron", withSecret: false, allowUnsigned: true);
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, null, null, body, CancellationToken.None);

        Assert.Equal(404, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Queue_backpressure_returns_503_without_enqueuing()
    {
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Queue_backpressure_returns_503_without_enqueuing), executor);
        var id = SeedTrigger(sp);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 500; i++)
                db.Jobs.Add(new Job { JobId = Guid.NewGuid(), Status = "pending", IsActive = true });
            db.SaveChanges();
        }
        var body = Encoding.UTF8.GetBytes("{}");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(503, outcome.StatusCode);
        Assert.Equal(0, executor.CallCount);
    }

    // ── payload-driven targeting ──────────────────────────────────────────────

    [Fact]
    public async Task Body_target_devices_are_ignored_unless_the_trigger_opts_in()
    {
        // The caller here holds only the webhook secret, and the run bypasses the
        // env/resource RBAC a manual run passes. So a leaked secret must not turn
        // into "run this workflow against any device in the inventory": body
        // targets are refused by default and the trigger's own list stands.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Body_target_devices_are_ignored_unless_the_trigger_opts_in), executor);
        var configured = Guid.NewGuid();
        var id = SeedTrigger(sp, targetDevices: new List<Guid> { configured });
        var attacker = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes($$"""{"target_devices":["{{attacker}}"]}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(new[] { configured }, executor.LastTargetDevices);
        // The payload is still preserved as input for templates.
        Assert.Equal(1, executor.LastInput.GetProperty("webhook").GetProperty("target_devices").GetArrayLength());
    }

    [Fact]
    public async Task Body_target_devices_are_honoured_when_the_trigger_opts_in_and_has_no_scope()
    {
        // allow_target_override on an unscoped trigger is the operator saying
        // "this webhook picks its own targets" — the pre-fix behaviour, now
        // opt-in. Keeps the "ping ran with no device" fix working.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(
            nameof(Body_target_devices_are_honoured_when_the_trigger_opts_in_and_has_no_scope), executor);
        var id = SeedTrigger(sp, allowTargetOverride: true);
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes($$"""{"target_devices":["{{d1}}","{{d2}}"]}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(new[] { d1, d2 }, executor.LastTargetDevices);
    }

    [Fact]
    public async Task Body_targets_can_only_narrow_a_scoped_trigger()
    {
        // Opted in AND scoped: the body picks a subset. The device the trigger
        // never listed is dropped rather than silently widening the blast radius.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Body_targets_can_only_narrow_a_scoped_trigger), executor);
        var inScope = Guid.NewGuid();
        var alsoInScope = Guid.NewGuid();
        var outOfScope = Guid.NewGuid();
        var id = SeedTrigger(
            sp, allowTargetOverride: true,
            targetDevices: new List<Guid> { inScope, alsoInScope });
        var body = Encoding.UTF8.GetBytes(
            $$"""{"target_devices":["{{inScope}}","{{outOfScope}}"]}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(new[] { inScope }, executor.LastTargetDevices);
    }

    [Fact]
    public async Task Body_pools_are_dropped_for_a_scoped_trigger()
    {
        // A pool's membership changes outside this request, so it can't be
        // checked against the trigger's device scope — refusing beats guessing.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Body_pools_are_dropped_for_a_scoped_trigger), executor);
        var inScope = Guid.NewGuid();
        var id = SeedTrigger(
            sp, allowTargetOverride: true, targetDevices: new List<Guid> { inScope });
        var body = Encoding.UTF8.GetBytes(
            $$"""{"target_devices":["{{inScope}}"],"target_pools":["{{Guid.NewGuid()}}"]}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Equal(new[] { inScope }, executor.LastTargetDevices);
        Assert.Empty(executor.LastTargetPools);
    }

    [Fact]
    public async Task Body_without_targets_falls_back_to_the_trigger_config()
    {
        // No body targets → the run uses whatever the trigger was configured with
        // (empty here), i.e. the prior behavior is preserved.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Body_without_targets_falls_back_to_the_trigger_config), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("""{"anything":"else"}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        Assert.Empty(executor.LastTargetDevices);
    }

    // ── (c) SSRF property: the ingest itself performs no outbound request ──────

    [Fact]
    public async Task Ingest_makes_no_outbound_call_even_with_an_internal_url_in_the_payload()
    {
        // A payload whose fields point at internal infra is inert at ingest — the
        // receiver only enqueues a run. Any use of such a URL by the fired
        // workflow is guarded downstream by IUrlGuard (UrlGuardSsrfTests). Here
        // we assert the receiver never fetched anything: no HttpClient is even
        // available to it, and the only side effect is the recorded enqueue with
        // the payload preserved verbatim as input.
        var executor = new RecordingExecutor();
        var sp = BuildProvider(nameof(Ingest_makes_no_outbound_call_even_with_an_internal_url_in_the_payload), executor);
        var id = SeedTrigger(sp);
        var body = Encoding.UTF8.GetBytes("""{"callback":"http://169.254.169.254/latest/meta-data/"}""");

        var outcome = await NewReceiver(sp, executor)
            .ReceiveAsync(id, HmacHeader(body, Secret), null, body, CancellationToken.None);

        Assert.Equal(202, outcome.StatusCode);
        // The URL travelled into the run input untouched (not fetched, not resolved).
        var webhook = executor.LastInput.GetProperty("webhook");
        Assert.Equal("http://169.254.169.254/latest/meta-data/", webhook.GetProperty("callback").GetString());
    }

    // Records enqueue calls; never orchestrates. ExecuteRunAsync is unreachable.
    private sealed class RecordingExecutor : IWorkflowExecutor
    {
        public int CallCount { get; private set; }
        public JsonElement LastInput { get; private set; }
        public List<Guid> LastTargetDevices { get; private set; } = new();
        public List<Guid> LastTargetPools { get; private set; } = new();

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId,
            RunWorkflowRequest request, CancellationToken ct, string trigger = "manual")
        {
            CallCount++;
            LastInput = request.Input;
            LastTargetDevices = request.TargetDevices;
            LastTargetPools = request.TargetPools;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotImplementedException();
    }
}
