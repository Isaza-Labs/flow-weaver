using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Policy;
using flow_weaver_backend.Services.Settings;
using flow_weaver_backend.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;

namespace flow_weaver_backend.Tests;

// "Allow everything / valid" doubles for the validation + policy + app-settings
// collaborators, so multi-dep services (WorkflowService, PromotionService) can be
// constructed and their read/guard paths exercised without the full graph.
internal sealed class FakeSchemaValidator : IWorkflowSchemaValidator
{
    public string CurrentSchemaVersion => "v1";
    public string RawJson => "{}";
    public WorkflowValidationResult Validate(System.Text.Json.JsonElement nodes, System.Text.Json.JsonElement edges)
        => WorkflowValidationResult.Ok();
}

internal sealed class FakeReferenceValidator : IWorkflowReferenceValidator
{
    public Task<WorkflowValidationResult> ValidateAsync(System.Text.Json.JsonElement nodes, CancellationToken ct, System.Text.Json.JsonElement? previousNodes = null)
        => Task.FromResult(WorkflowValidationResult.Ok());
    public Task<WorkflowValidationResult> ValidateWithContextAsync(System.Text.Json.JsonElement nodes, string? workflowName, string? workflowDescription, CancellationToken ct, System.Text.Json.JsonElement? previousNodes = null)
        => Task.FromResult(WorkflowValidationResult.Ok());
}

internal sealed class FakeVendorCommandValidator : IVendorCommandValidator
{
    public Task<WorkflowValidationResult> ValidateAsync(System.Text.Json.JsonElement nodes, IReadOnlyCollection<Guid> targetDeviceIds, CancellationToken ct)
        => Task.FromResult(WorkflowValidationResult.Ok());
    public Task<IReadOnlyList<string>> ValidateCommandsAsync(string deviceType, IReadOnlyList<string> commands, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

// Grants every capability, like an admin with no transport ceiling.
internal sealed class AllowAllEffectivePermissions : flow_weaver_backend.Services.Permission.IEffectivePermissions
{
    public Task<bool> HasAsync(string capability, flow_weaver_backend.Services.Permission.PermissionContext ctx, CancellationToken ct = default)
        => Task.FromResult(true);
    public Task<bool> HasAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(
            flow_weaver_backend.Services.Permission.Catalog.CapabilityCatalog.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
}

internal sealed class FakePolicyEvaluator : IPolicyEvaluator
{
    public Task<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken ct)
        => Task.FromResult(new PolicyDecision(true, null, null));
}

internal sealed class FakeAppSettings : IAppSettingsService
{
    public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(AppSettings.Default);
    public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default) => Task.FromResult(updated);
}

// No-op dual-write of the built-in permission grants. The real BuiltinGrantSync
// needs a live DB + catalogue; user-CRUD handler/controller tests only assert
// their own behaviour, so the sync side-effect is stubbed out.
internal sealed class NoOpBuiltinGrantSync : flow_weaver_backend.Services.Permission.IBuiltinGrantSync
{
    public Task EnsureGrantsAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SyncUserAsync(Guid userId, string role, CancellationToken ct = default) => Task.CompletedTask;
}

// Vendor-command catalog cache — VendorCommandService only calls Invalidate
// after a mutation, so the lookup methods can return neutral defaults.
internal sealed class FakeVendorCommandRegistry : IVendorCommandRegistry
{
    public Task<KnownStatus> IsKnownAsync(string deviceType, string command, CancellationToken ct)
        => Task.FromResult(default(KnownStatus));
    public Task<IReadOnlyList<string>> SuggestSimilarAsync(string deviceType, string command, int max, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<string>> KnownCommandsForDeviceTypeAsync(string deviceType, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public void Invalidate() { }
}

// File-backed catalog caches — the real ones read Skills/*.md and Specs/*.yaml.
// Services only call Invalidate/Reload after a mutation, so no-ops are enough.
internal sealed class FakeSkillPromptLoader : ISkillPromptLoader
{
    public Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default) => Task.FromResult(string.Empty);
    public void Invalidate() { }
}

internal sealed class FakeApiSpecIndex : IApiSpecIndex
{
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<ApiOperation> All() => Array.Empty<ApiOperation>();
    public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => Array.Empty<ApiOperation>();
    public ApiOperation? GetByOperationId(string operationId) => null;
}

// The observability repositories (audit / auth / trace) resolve an
// AppDbContext from their own DI scope so an append-only write can never flush
// or poison the caller's unit of work. Tests hand them a single in-memory
// context instead: they assert on the ROWS that land, and EF InMemory has no
// transaction to isolate anyway.
//
// The isolation itself — that a write here does not commit the caller's
// pending changes — is covered explicitly by ObservabilityIsolationTests,
// which builds a real ServiceProvider so each scope gets its own context.
internal static class TestScopes
{
    public static IServiceScopeFactory Over(AppDbContext db) => new SingleContextScopeFactory(db);

    private sealed class SingleContextScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly AppDbContext _db;
        public SingleContextScopeFactory(AppDbContext db) => _db = db;

        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(AppDbContext) ? _db : null;
        public void Dispose() { /* the test owns the context's lifetime */ }
    }
}

// No-op audit trail + trace logger for services that record side-effects.
internal sealed class FakeAudit : IAuditLogger
{
    public Task LogAsync(string entityType, Guid? entityId, string action,
        object? before = null, object? after = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class FakeTrace : ITraceLogger
{
    public Task<Guid> StartAsync(string action, string category, object? metadata = null, CancellationToken ct = default)
        => Task.FromResult(Guid.NewGuid());
    public Task CompleteAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task FailAsync(Guid traceEventId, string error, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimeoutAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task EventAsync(string action, string category, string status, object? metadata = null, string? error = null, CancellationToken ct = default) => Task.CompletedTask;
}

// Passthrough encryption so credential/provider services round-trip without
// a real DataProtection keyring. Ciphertext == UTF-8 bytes of the plaintext.
internal sealed class FakeCrypto : ICredentialEncryptionService
{
    public byte[]? Encrypt(string? plaintext)
        => plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);
    public string? Decrypt(byte[]? ciphertext)
        => ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext);
}

// A ${secret:...} store for handler tests. Markers listed in `Values` resolve
// to their plaintext; anything else is left LITERAL, which is what the real
// SecretResolver does and what makes "the reference was never resolved" visible
// as an auth failure naming the marker instead of as an empty password.
internal sealed class PassThroughSecrets : flow_weaver_backend.Services.Ai.Secrets.ISecretResolver
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    // Every template this resolver was asked to substitute, in order.
    public List<string> Seen { get; } = new();

    public PassThroughSecrets(params (string Marker, string Value)[] values)
    {
        foreach (var (marker, value) in values) Values[marker] = value;
    }

    public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct)
        => Task.FromResult(Values.TryGetValue($"${{secret:{source}:{idOrName}:{field}}}", out var v) ? v : null);

    public Task<string> SubstituteAsync(string template, CancellationToken ct)
    {
        Seen.Add(template);
        var result = template;
        foreach (var (marker, value) in Values) result = result.Replace(marker, value, StringComparison.Ordinal);
        return Task.FromResult(result);
    }
}

// No-op job queue (the real one is Dapper/Npgsql and needs a live DB).
internal sealed class FakeQueue : IQueueRepository
{
    public List<Guid> Enqueued { get; } = new();
    public List<Guid> Cancelled { get; } = new();

    public Task<Job?> ClaimAsync(string[] tags, string workerId, CancellationToken ct) => Task.FromResult<Job?>(null);
    public Task CompleteAsync(Guid jobId, CancellationToken ct) => Task.CompletedTask;
    public Task FailAsync(Guid jobId, string error, CancellationToken ct) => Task.CompletedTask;
    public Task<Guid> EnqueueAsync(string type, System.Text.Json.JsonElement payload, string tag, int priority, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        Enqueued.Add(id);
        return Task.FromResult(id);
    }
    public Task<int> ReclaimExpiredAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, int leaseSeconds, CancellationToken ct) => Task.FromResult(true);
    public Task<int> CancelPendingByRunAsync(Guid workflowRunId, CancellationToken ct)
    {
        Cancelled.Add(workflowRunId);
        return Task.FromResult(0);
    }
}

// Shared test infrastructure for the coverage push. Keeps every new test
// file free of boilerplate: a fresh isolated InMemory AppDbContext per test,
// and a scriptable HttpClient/IHttpClientFactory for services that make
// outbound calls (AI providers, REST executor, messaging, integration health).

internal static class TestDb
{
    // Fresh InMemory database, unique per call so tests never share state.
    // Pass a stable name only when two contexts must see the same store.
    public static AppDbContext NewContext(string? name = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            // The InMemory provider has no transactions and throws on
            // BeginTransaction by default. Downgrade that to a no-op so
            // services wrapping their writes in IUnitOfWork.ExecuteInTransaction
            // (integration bundle, promotion) are testable. Atomicity itself is
            // integration territory — these tests assert the staged writes.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }
}

// HttpMessageHandler whose responses are supplied by a delegate, so a test
// can assert on the outgoing request and shape the reply. Records every
// request for post-hoc assertions.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    // Convenience: always answer with the given status + JSON/text body.
    public FakeHttpMessageHandler(HttpStatusCode status, string body = "", string contentType = "application/json")
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        })
    { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        return _responder(request);
    }
}

// No-op SSRF guard for fixtures that don't exercise URL blocking.
internal sealed class PassUrlGuard : flow_weaver_backend.Services.Net.IUrlGuard
{
    public void EnsureSafe(string url, bool allowPrivate = false) { }
    public void EnsureHostSafe(string host, bool allowPrivate = false) { }
}

// Records OAuth token-cache invalidations (IntegrationService invalidates on
// auth_config updates). GetAccessTokenAsync returns a fixed token — fixtures
// that exercise real grants use TestAuth.Applier with a scripted handler.
internal sealed class FakeOAuthTokens : flow_weaver_backend.Services.Integration.IIntegrationOAuthTokenService
{
    public List<Guid> Invalidated { get; } = new();
    public Task<string> GetAccessTokenAsync(Integration integration, CancellationToken ct = default)
        => Task.FromResult("fake-token");
    public void Invalidate(Guid integrationId) => Invalidated.Add(integrationId);
}

// Real async auth applier wired to fakes. Non-OAuth integrations never touch
// the token service; fixtures that DO exercise oauth2_client_credentials pass
// their own `tokenHandler` to script the token endpoint.
internal static class TestAuth
{
    public static flow_weaver_backend.Services.Integration.IntegrationAuthApplier Applier(
        FakeHttpMessageHandler? tokenHandler = null) => new(
        new flow_weaver_backend.Services.Integration.IntegrationAuthBuilder(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<flow_weaver_backend.Services.Integration.IntegrationAuthBuilder>.Instance),
        new flow_weaver_backend.Services.Integration.IntegrationOAuthTokenService(
            new FakeHttpClientFactory(tokenHandler ?? new FakeHttpMessageHandler(HttpStatusCode.OK, "{}")),
            new PassUrlGuard(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<flow_weaver_backend.Services.Integration.IntegrationOAuthTokenService>.Instance));
}

// IHttpClientFactory that hands out clients backed by a single fake handler.
// Optional baseAddress for services that build relative URIs.
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly Uri? _baseAddress;

    public FakeHttpClientFactory(FakeHttpMessageHandler handler, string? baseAddress = null)
    {
        _handler = handler;
        _baseAddress = baseAddress is null ? null : new Uri(baseAddress);
    }

    public HttpClient CreateClient(string name)
        => new(_handler, disposeHandler: false) { BaseAddress = _baseAddress };
}

// Controllers that read identity from ClaimsPrincipal (not ICurrentUser) need
// an authenticated HttpContext. Claims mirror FakeUser's fixed GUIDs.
internal static class TestCtx
{
    public static ControllerContext WithUser(Guid? userId = null, string role = "admin")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (userId ?? new Guid("22222222-2222-2222-2222-222222222222")).ToString()),
            new(ClaimTypes.Role, role),
        };
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
        };
    }
}

// Minimal IHostEnvironment for services that resolve paths off ContentRootPath
// (e.g. GitService's Git:Root default). Points at the OS temp dir.
internal sealed class FakeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "flow_weaver_backend.Tests";
    public string ContentRootPath { get; set; } = System.IO.Path.GetTempPath();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

internal static class TestJson
{
    // Parse a JSON literal into a JsonElement (deep-cloned so it survives the
    // owning JsonDocument being disposed).
    public static JsonElement Element(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
