namespace flow_weaver_backend.Services.Observability;

// Application-level audit trail. Every critical path opens a scope with
// StartAsync and closes it with CompleteAsync / FailAsync / TimeoutAsync;
// instantaneous events that have no duration (e.g. "auth.login") use
// EventAsync instead.
//
// Implementations must never throw — tracing failures cannot take down
// the user-facing request they're measuring.
public interface ITraceLogger
{
    // Opens a span. Returns the generated TraceEventId so the caller can
    // pass it to Complete/Fail. The row is persisted immediately with
    // status="started" so a crashed handler still leaves a breadcrumb.
    Task<Guid> StartAsync(
        string action,
        string category,
        object? metadata = null,
        CancellationToken ct = default);

    // Closes a span with status="completed", computing duration_ms from
    // the original StartedAt. Optional metadata merges with whatever the
    // caller passed on Start (right-side wins per jsonb concat semantics).
    Task CompleteAsync(
        Guid traceEventId,
        object? metadata = null,
        CancellationToken ct = default);

    // Closes a span with status="failed". `error` is stored in the row's
    // ErrorMessage column so filter-by-failure can hit it directly without
    // scanning metadata jsonb.
    Task FailAsync(
        Guid traceEventId,
        string error,
        object? metadata = null,
        CancellationToken ct = default);

    Task TimeoutAsync(
        Guid traceEventId,
        object? metadata = null,
        CancellationToken ct = default);

    // Fire-and-forget single-shot event. Used for things that have no
    // duration worth measuring (successful logins, user-triggered page
    // visits if we wanted, secret rotations).
    Task EventAsync(
        string action,
        string category,
        string status,
        object? metadata = null,
        string? error = null,
        CancellationToken ct = default);
}
