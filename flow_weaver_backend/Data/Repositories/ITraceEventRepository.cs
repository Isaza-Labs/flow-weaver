using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// TraceEvent persistence. Append (Start/Event) + close-by-id (Complete/Fail/
// Timeout). Close matches on the trace id alone: trace ids are globally
// unique Guids, so background flows can close a trace with nothing else.
//
// Both operations are atomic and run against their own DbContext rather than
// the caller's. TraceLogger swallows persistence failures by design, and with a
// shared context that swallowed failure left the rejected row sitting in the
// caller's ChangeTracker in Added state — so the NEXT business SaveChanges
// retried it and failed, taking down a real request. Exactly what the
// swallow-and-warn policy exists to prevent.
public interface ITraceEventRepository
{
    Task AddAsync(TraceEvent row, CancellationToken ct = default);

    // Loads the row by trace id, applies `mutate`, and persists — all in one
    // scope. Returns false when the id doesn't exist (an orphaned close).
    Task<bool> UpdateAsync(Guid traceEventId, Action<TraceEvent> mutate, CancellationToken ct = default);
}
