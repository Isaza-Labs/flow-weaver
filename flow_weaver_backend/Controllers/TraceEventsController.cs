using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Admin read surface for the application trace table. Covers HTTP request
// traces as well as the rows emitted by background workers, seeders and the
// bootstrap sequence — the only way to debug worker / system / scheduler
// paths that run without an HTTP request.
//
// Live debugging workflow: poll this endpoint every few seconds with a
// status=started filter to see in-flight operations, or filter by
// request_id to reconstruct a single HTTP call end-to-end.
[ApiController]
[Route("api/admin/traces")]
[Authorize(Policy = "Admin")]
public class TraceEventsController : ControllerBase
{
    private const int MaxLimit = 500;
    private const int DefaultLimit = 100;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public TraceEventsController(AppDbContext db, ICurrentUser caller)
    {
        _db = db;
        _caller = caller;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<TraceEventResponse>>> List(
        [FromQuery] string? category,
        [FromQuery] string? action,
        [FromQuery] string? status,
        [FromQuery] Guid? user_id,
        [FromQuery] string? request_id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, MaxLimit);

        var query = _db.TraceEvents.AsNoTracking()
            .Where(t => t.IsActive);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(t => t.Action == action);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);
        if (user_id.HasValue)
            query = query.Where(t => t.UserId == user_id.Value);
        if (!string.IsNullOrWhiteSpace(request_id))
            query = query.Where(t => t.RequestId == request_id);
        if (from.HasValue)
            query = query.Where(t => t.At >= from.Value);
        if (to.HasValue)
            query = query.Where(t => t.At <= to.Value);

        var rows = await query
            .OrderByDescending(t => t.At)
            .Skip(Math.Max(offset, 0))
            .Take(clamped)
            .ToListAsync(ct);

        return Ok(rows.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<TraceEventResponse>> Get(Guid id, CancellationToken ct)
    {
        var row = await _db.TraceEvents.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TraceEventId == id, ct);
        return row is null ? NotFound() : Ok(ToResponse(row));
    }

    // Aggregate counts per category — useful for /admin dashboards when
    // we want a quick "what's busiest" tile without scanning every row.
    [HttpGet("summary")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<object>> Summary(
        [FromQuery] int hours = 24, CancellationToken ct = default)
    {
        var window = Math.Clamp(hours, 1, 24 * 30);
        var from = DateTime.UtcNow.AddHours(-window);

        var byCategory = await _db.TraceEvents.AsNoTracking()
            .Where(t => t.IsActive
                        && t.At >= from)
            .GroupBy(t => t.Category)
            .Select(g => new { category = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var byStatus = await _db.TraceEvents.AsNoTracking()
            .Where(t => t.IsActive
                        && t.At >= from)
            .GroupBy(t => t.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync(ct);

        return Ok(new
        {
            window_hours = window,
            from,
            by_category = byCategory,
            by_status = byStatus,
        });
    }

    private static TraceEventResponse ToResponse(Models.TraceEvent t) => new()
    {
        TraceEventId = t.TraceEventId,
        UserId = t.UserId,
        RequestId = t.RequestId,
        Action = t.Action,
        Category = t.Category,
        Status = t.Status,
        DurationMs = t.DurationMs,
        ErrorMessage = t.ErrorMessage,
        Metadata = t.Metadata,
        At = t.At,
    };
}

public class TraceEventResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("trace_event_id")]
    public Guid TraceEventId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("duration_ms")]
    public int? DurationMs { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("metadata")]
    public JsonElement Metadata { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("at")]
    public DateTime At { get; set; }
}
