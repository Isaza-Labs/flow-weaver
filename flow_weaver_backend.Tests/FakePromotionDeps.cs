using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Policy;

namespace flow_weaver_backend.Tests;

// Minimal stubs for the dependencies PromotionService takes that aren't
// under test in the Sprint 16 harness-gate suite. Each one no-ops or
// returns the trivially-permissive answer so the test can focus on the
// simulation gate logic.

internal sealed class NoOpAuditLogger : IAuditLogger
{
    public Task LogAsync(
        string entityType, Guid? entityId, string action,
        object? before = null, object? after = null,
        CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoOpTraceLogger : ITraceLogger
{
    public Task<Guid> StartAsync(string action, string category, object? metadata = null, CancellationToken ct = default)
        => Task.FromResult(Guid.NewGuid());

    public Task CompleteAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task FailAsync(Guid traceEventId, string error, object? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task TimeoutAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task EventAsync(string action, string category, string status, object? metadata = null, string? error = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

// Always allows. Tests that want to verify policy interaction should use
// the real PolicyEvaluator with seeded rows.
internal sealed class AlwaysAllowPolicyEvaluator : IPolicyEvaluator
{
    public Task<PolicyDecision> EvaluateAsync(
        PolicyEvaluationContext context, CancellationToken ct)
        => Task.FromResult(new PolicyDecision(true, null, null));
}
