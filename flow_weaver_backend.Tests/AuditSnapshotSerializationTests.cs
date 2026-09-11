using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Service mappers use `dto.X ?? default`, so a JsonElement member of an audit
// projection can be Undefined when the caller never supplied the field. The
// HTTP response pipeline neutralises that with SafeJsonElementConverter; the
// AuditLogger must do the same, or the snapshot serialisation throws
// InvalidOperationException AFTER the mutation committed and the request 500s
// (seen live on POST /api/devicepool with no filterRules in the body).
public class AuditSnapshotSerializationTests : IDisposable
{
    private static readonly Guid User = new("33333333-3333-3333-3333-333333333333");

    private readonly ServiceProvider _sp;

    public AuditSnapshotSerializationTests()
    {
        // Captured outside the lambda: options are built per scope, so an
        // inline NewGuid would give every scope its own empty database.
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddLogging();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AuditLogger>
    {
        public List<Exception?> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                Errors.Add(new Exception(formatter(state, exception), exception));
        }
    }

    private readonly CapturingLogger _log = new();

    private AuditLogger BuildAudit() => new(
        new AuditEventRepository(_sp.GetRequiredService<IServiceScopeFactory>()),
        new FakeUser { UserId = User, Username = "ada" },
        new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
        _log);

    private AppDbContext VerifyContext() =>
        _sp.GetRequiredService<IServiceScopeFactory>()
           .CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task Snapshot_WithUndefinedJsonElement_WritesRowWithJsonNullMember()
    {
        var snapshot = new
        {
            name = "pool-a",
            filter_rules = default(JsonElement), // ValueKind.Undefined
        };

        // Must not throw — the mutation this audits already committed.
        await BuildAudit().LogAsync("device_pool", Guid.NewGuid(), "create", after: snapshot);

        using var verify = VerifyContext();
        Assert.True(_log.Errors.Count == 0,
            "audit errors: " + string.Join(" | ", _log.Errors.Select(e => e?.ToString())));
        var row = Assert.Single(verify.AuditLogs);
        Assert.Equal("pool-a", row.AfterJson.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.AfterJson.GetProperty("filter_rules").ValueKind);
    }

    private sealed class ThrowsOnRead
    {
        public string Boom => throw new InvalidOperationException("unserializable");
    }

    [Fact]
    public async Task Snapshot_ThatFailsToSerialize_StillWritesRowWithNullSnapshot()
    {
        await BuildAudit().LogAsync(
            "device_pool", Guid.NewGuid(), "update",
            before: new { name = "pool-a" }, after: new ThrowsOnRead());

        using var verify = VerifyContext();
        var row = Assert.Single(verify.AuditLogs);
        // The intact side survives; the broken side degrades to JSON null.
        Assert.Equal("pool-a", row.BeforeJson.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, row.AfterJson.ValueKind);
    }
}
