using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// The observability writers (audit / auth / trace) persist through their OWN
// AppDbContext rather than the caller's. These tests build a real
// ServiceProvider — not the single-context TestScopes shim other tests use —
// because the property under test IS that the two contexts are different.
//
// Two bugs live here, and both were silent:
//
//   1. AuditEventRepository called SaveChangesAsync on the shared scoped
//      context, so writing an audit row committed whatever the caller had
//      staged so far. WorkflowImportController stages integrations, snippets
//      and actions and commits once at the end; an audit write in the middle
//      persisted the partial set, and a later validation failure left it
//      behind with no rollback.
//
//   2. TraceLogger swallows persistence failures by design ("a broken trace
//      table must never break a user request"), but with a shared context the
//      rejected row stayed in the caller's ChangeTracker in Added state — so
//      the next business SaveChanges retried it, failed, and took down a real
//      request. Exactly what the swallow was there to prevent.
public class ObservabilityIsolationTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Sp { get; }

        public Fixture()
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddLogging();
            Sp = services.BuildServiceProvider();
        }

        public IServiceScopeFactory Scopes => Sp.GetRequiredService<IServiceScopeFactory>();

        public AppDbContext NewContext() =>
            Sp.GetRequiredService<IServiceScopeFactory>()
              .CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        public void Dispose() => Sp.Dispose();
    }

    private static AuditLogger BuildAudit(Fixture f, HttpContext? http)
    {
        var accessor = new HttpContextAccessor { HttpContext = http };
        return new AuditLogger(
            new AuditEventRepository(f.Scopes),
            new FakeUser { UserId = User, Username = "ada" },
            accessor,
            NullLogger<AuditLogger>.Instance);
    }

    // ─── 1. The caller's unit of work is untouched ──────────────────────

    [Fact]
    public async Task AuditWrite_DoesNotCommitTheCallersPendingChanges()
    {
        using var f = new Fixture();

        // A caller mid-transaction: entity staged, deliberately not saved yet.
        var caller = f.NewContext();
        caller.Workflows.Add(new Workflow
        {
            WorkflowId = Guid.NewGuid(),
            Name = "half-imported",
            Environment = "draft",
            Version = 1,
            IsActive = true,
            Nodes = TestJson.Element("[]"),
            Edges = TestJson.Element("[]"),
        });

        await BuildAudit(f, new DefaultHttpContext())
            .LogAsync("workflow", Guid.NewGuid(), "create", after: new { name = "x" });

        // The audit row landed…
        using var verify = f.NewContext();
        Assert.Single(verify.AuditLogs);
        // …and the caller's staged row did NOT.
        Assert.Empty(verify.Workflows);
        Assert.Single(caller.ChangeTracker.Entries<Workflow>());
    }

    [Fact]
    public async Task AuthEventWrite_DoesNotCommitTheCallersPendingChanges()
    {
        using var f = new Fixture();

        var caller = f.NewContext();
        caller.Users.Add(new User
        {
            UserId = Guid.NewGuid(),
            Username = "pending",
            Email = "p@example.com",
            Role = "viewer",
            IsActive = true,
        });

        await new AuthEventRepository(f.Scopes).AddAsync(new AuthEvent
        {
            AuthEventId = Guid.NewGuid(),
            Event = "login_failure",
            Ip = "10.0.0.1",
            UserAgent = "curl",
            At = DateTime.UtcNow,
            Metadata = TestJson.Element("{}"),
        });

        using var verify = f.NewContext();
        Assert.Single(verify.AuthEvents);
        Assert.Empty(verify.Users);
    }

    [Fact]
    public async Task TraceWrite_DoesNotCommitTheCallersPendingChanges()
    {
        using var f = new Fixture();

        var caller = f.NewContext();
        caller.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "staged",
            IpAddress = "10.0.0.1",
            Platform = "cisco_ios",
            IsActive = true,
        });

        await new TraceEventRepository(f.Scopes).AddAsync(new TraceEvent
        {
            TraceEventId = Guid.NewGuid(),
            Action = "worker.dispatch",
            Category = "worker",
            Status = "started",
            Metadata = TestJson.Element("{}"),
            IsActive = true,
            At = DateTime.UtcNow,
        });

        using var verify = f.NewContext();
        Assert.Single(verify.TraceEvents);
        Assert.Empty(verify.Devices);
    }

    // ─── 2. A failing writer stays contained ────────────────────────────

    private sealed class ThrowingTraceRepository : ITraceEventRepository
    {
        public Task AddAsync(TraceEvent row, CancellationToken ct = default)
            => throw new InvalidOperationException("trace table unavailable");
        public Task<bool> UpdateAsync(Guid traceEventId, Action<TraceEvent> mutate, CancellationToken ct = default)
            => throw new InvalidOperationException("trace table unavailable");
    }

    private static TraceLogger BuildTrace(ITraceEventRepository repo) => new(
        repo,
        new FakeUser { UserId = User },
        new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
        NullLogger<TraceLogger>.Instance);

    [Fact]
    public async Task TraceStart_SwallowsAPersistenceFailure()
    {
        var traceId = await BuildTrace(new ThrowingTraceRepository())
            .StartAsync("worker.dispatch", "worker");

        // Still hands back an id so the caller's Complete/Fail path is
        // well-defined; the close simply finds nothing.
        Assert.NotEqual(Guid.Empty, traceId);
    }

    [Fact]
    public async Task TraceClose_SwallowsAPersistenceFailure()
    {
        // Must not throw — the whole point of the swallow-and-warn policy.
        await BuildTrace(new ThrowingTraceRepository()).CompleteAsync(Guid.NewGuid());
        await BuildTrace(new ThrowingTraceRepository()).FailAsync(Guid.NewGuid(), "boom");
    }

    [Fact]
    public async Task TraceEvent_SwallowsAPersistenceFailure()
    {
        await BuildTrace(new ThrowingTraceRepository())
            .EventAsync("secret.access", "security", "completed");
    }

    // Closing a trace id that was never written (its Add failed, or it belongs
    // to a purged row) reports "not found" instead of throwing.
    [Fact]
    public async Task TraceUpdate_ReportsFalseForAnUnknownId()
    {
        using var f = new Fixture();

        var found = await new TraceEventRepository(f.Scopes)
            .UpdateAsync(Guid.NewGuid(), _ => { });

        Assert.False(found);
    }

    [Fact]
    public async Task TraceUpdate_AppliesTheMutationAndPersistsIt()
    {
        using var f = new Fixture();
        var repo = new TraceEventRepository(f.Scopes);
        var id = Guid.NewGuid();

        await repo.AddAsync(new TraceEvent
        {
            TraceEventId = id,
            Action = "worker.dispatch",
            Category = "worker",
            Status = "started",
            Metadata = TestJson.Element("{}"),
            IsActive = true,
            At = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });

        var found = await repo.UpdateAsync(id, row =>
        {
            row.Status = "completed";
            row.DurationMs = 42;
        });

        Assert.True(found);
        using var verify = f.NewContext();
        var stored = verify.TraceEvents.Single();
        Assert.Equal("completed", stored.Status);
        Assert.Equal(42, stored.DurationMs);
    }

    // ─── 3. Audit row contents ──────────────────────────────────────────

    // The correlation id is the X-Request-Id CorrelationMiddleware stamps on
    // the response — the same value TraceEvent.RequestId and every Serilog
    // line carry, so the three sources can be joined. It used to be
    // ctx.TraceIdentifier, which is a different id entirely.
    [Fact]
    public async Task AuditRow_CarriesTheCorrelationIdSharedWithTracesAndLogs()
    {
        using var f = new Fixture();
        var http = new DefaultHttpContext();
        http.Response.Headers[CorrelationMiddleware.RequestIdHeader] = "corr-42";
        http.TraceIdentifier = "0HN7:00000001"; // the old, non-joinable value

        await BuildAudit(f, http).LogAsync("workflow", Guid.NewGuid(), "create");

        using var verify = f.NewContext();
        Assert.Equal("corr-42", verify.AuditLogs.Single().RequestId);
    }

    [Fact]
    public async Task AuditRow_CarriesTheActorForASignedInUser()
    {
        using var f = new Fixture();

        await BuildAudit(f, new DefaultHttpContext()).LogAsync("credential", Guid.NewGuid(), "create");

        using var verify = f.NewContext();
        var row = verify.AuditLogs.Single();
        Assert.Equal("ada", row.Actor);
        Assert.Equal(User, row.UserId);
    }

    // Automation binds a synthetic identity with no user id. Actor is the only
    // thing that then tells "workflow-runner" apart from "git-webhook".
    [Fact]
    public async Task AuditRow_CarriesTheActorForAutomationWithNoUserId()
    {
        using var f = new Fixture();
        var logger = new AuditLogger(
            new AuditEventRepository(f.Scopes),
            new FakeUser { UserId = Guid.Empty, Username = "workflow-runner" },
            new HttpContextAccessor { HttpContext = null },
            NullLogger<AuditLogger>.Instance);

        await logger.LogAsync("git_repository", Guid.NewGuid(), "update");

        using var verify = f.NewContext();
        var row = verify.AuditLogs.Single();
        Assert.Equal("workflow-runner", row.Actor);
        Assert.Null(row.UserId);
    }

    // The mutation is committed before the audit row is written, so throwing
    // here would answer a request that already took effect with a 500 — the
    // client retries and applies the change twice. Losing the row is bad;
    // lying about whether the change landed is worse. `audit.write.failed` is
    // logged at Error so the hole in the trail is alertable.
    private sealed class ThrowingAuditRepository : IAuditEventRepository
    {
        public Task AddAsync(AuditEvent entry, CancellationToken ct = default)
            => throw new InvalidOperationException("audit table unavailable");
    }

    [Fact]
    public async Task AuditWrite_DoesNotThrowWhenPersistenceFails()
    {
        var logger = new AuditLogger(
            new ThrowingAuditRepository(),
            new FakeUser { UserId = User, Username = "ada" },
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            NullLogger<AuditLogger>.Instance);

        await logger.LogAsync("workflow", Guid.NewGuid(), "create");
    }
}
