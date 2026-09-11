using System.Security.Claims;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Http;

namespace flow_weaver_backend.Services.Observability;

// Writes TraceEvent rows + mirrors each call into Serilog so one source
// of truth covers both the DB-backed timeline and the live `docker logs`
// feed. All persistence failures are caught and logged — a broken trace
// table must never break a user request. That promise only holds because
// ITraceEventRepository persists through its own DbContext; see the note
// on the interface.
//
// The user field comes from ICurrentUser where available, falling back to
// the raw name claim; rows from unattributed background flows carry none.
public sealed class TraceLogger : ITraceLogger
{
    private readonly ITraceEventRepository _traces;
    private readonly ICurrentUser _caller;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<TraceLogger> _logger;

    public TraceLogger(
        ITraceEventRepository traces,
        ICurrentUser caller,
        IHttpContextAccessor http,
        ILogger<TraceLogger> logger)
    {
        _traces = traces;
        _caller = caller;
        _http = http;
        _logger = logger;
    }

    public async Task<Guid> StartAsync(
        string action, string category, object? metadata = null, CancellationToken ct = default)
    {
        var userId = ResolveUserId();
        var traceId = Guid.NewGuid();

        var row = new Models.TraceEvent
        {
            TraceEventId = traceId,
            UserId = userId,
            RequestId = CorrelationMiddleware.CurrentRequestId(_http.HttpContext),
            Action = action,
            Category = category,
            Status = "started",
            DurationMs = null,
            Metadata = ToJson(metadata),
            IsActive = true,
            At = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _logger.LogInformation(
            "trace.start action={Action} category={Category} trace_event_id={TraceEventId}",
            action, category, traceId);

        await SafeAsync(() => _traces.AddAsync(row, ct));
        return traceId;
    }

    public Task CompleteAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default)
        => CloseAsync(traceEventId, "completed", error: null, metadata, ct);

    public Task FailAsync(Guid traceEventId, string error, object? metadata = null, CancellationToken ct = default)
        => CloseAsync(traceEventId, "failed", error, metadata, ct);

    public Task TimeoutAsync(Guid traceEventId, object? metadata = null, CancellationToken ct = default)
        => CloseAsync(traceEventId, "timeout", error: null, metadata, ct);

    public async Task EventAsync(
        string action, string category, string status,
        object? metadata = null, string? error = null, CancellationToken ct = default)
    {
        var userId = ResolveUserId();

        var row = new Models.TraceEvent
        {
            TraceEventId = Guid.NewGuid(),
            UserId = userId,
            RequestId = CorrelationMiddleware.CurrentRequestId(_http.HttpContext),
            Action = action,
            Category = category,
            Status = status,
            DurationMs = null,
            ErrorMessage = Truncate(error, 2000),
            Metadata = ToJson(metadata),
            IsActive = true,
            At = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _logger.LogInformation(
            "trace.event action={Action} category={Category} status={Status}",
            action, category, status);

        await SafeAsync(() => _traces.AddAsync(row, ct));
    }

    // ─── internals ──────────────────────────────────────────────────────

    private async Task CloseAsync(
        Guid traceEventId, string status, string? error, object? metadata, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string? action = null;
        int? durationMs = null;

        var found = await SafeAsync(() => _traces.UpdateAsync(traceEventId, row =>
        {
            row.Status = status;
            row.DurationMs = (int)Math.Max(0, (now - row.CreatedAt).TotalMilliseconds);
            if (error is not null) row.ErrorMessage = Truncate(error, 2000);
            if (metadata is not null) row.Metadata = ToJson(metadata);
            row.At = now;
            row.UpdatedAt = now;
            action = row.Action;
            durationMs = row.DurationMs;
        }, ct));

        if (!found)
        {
            _logger.LogWarning(
                "trace.close.orphan trace_event_id={TraceEventId} status={Status}",
                traceEventId, status);
            return;
        }

        _logger.LogInformation(
            "trace.close action={Action} status={Status} duration_ms={DurationMs} trace_event_id={TraceEventId}",
            action, status, durationMs, traceEventId);
    }

    // The acting user, or null for background work that has no caller.
    private Guid? ResolveUserId()
    {
        try
        {
            if (_caller.IsAuthenticated)
                return _caller.UserId;

            if (_http.HttpContext?.User is { } user)
            {
                // Some paths (bootstrap, test endpoints) are authenticated
                // enough to have claims but don't hydrate ICurrentUser
                // on demand. Read the raw claim as a fallback.
                var us = user.FindFirstValue(ClaimTypes.NameIdentifier);
                if (Guid.TryParse(us, out var uparsed)) return uparsed;
            }
        }
        catch
        {
            // Background services run without a caller — swallow and fall
            // through with null so the row still lands.
        }
        return null;
    }

    private static JsonElement ToJson(object? value)
    {
        if (value is null) return JsonDocument.Parse("null").RootElement;
        try { return JsonSerializer.SerializeToElement(value); }
        catch { return JsonSerializer.SerializeToElement(new { serialize_error = value.GetType().Name }); }
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s.Substring(0, max);

    private async Task SafeAsync(Func<Task> save)
    {
        try { await save(); }
        catch (Exception ex)
        {
            // Don't let the trace table take down a real request. Surface
            // at Warning so ops can notice if it's systemic.
            _logger.LogWarning(ex, "trace.persist.failed");
        }
    }

    // Same policy for operations whose result we need: a persistence failure
    // reports "not found" rather than propagating, so the caller logs an
    // orphan warning instead of throwing.
    private async Task<bool> SafeAsync(Func<Task<bool>> save)
    {
        try { return await save(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "trace.persist.failed");
            return false;
        }
    }
}
