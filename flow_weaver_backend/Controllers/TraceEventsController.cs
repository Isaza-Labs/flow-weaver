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

    /// <param name="search">
    /// One free-text term over every text a trace carries — action, category, error
    /// message, request id and the metadata blob. Substring, not equality: people
    /// arrive here holding a fragment they saw in a log, not a whole dotted name.
    /// </param>
    /// <param name="user">
    /// Who fired it, by whatever the caller happens to have: the user id, or part of a
    /// username or email, resolved through a join to users.
    /// </param>
    /// <param name="min_duration_ms">
    /// Only rows that finished and took at least this long. Also flips the ordering to
    /// slowest-first — a time-ordered page of a wide window does not contain the slow
    /// rows this filter was set to find.
    /// </param>
    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<TraceEventResponse>>> List(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? user,
        [FromQuery] string? request_id,
        [FromQuery] int? min_duration_ms,
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
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);
        // Exact, and kept next to `search`: this is the deep link that reconstructs one
        // request, where the same id in the search box would also pull in every row
        // that merely mentions it.
        if (!string.IsNullOrWhiteSpace(request_id))
            query = query.Where(t => t.RequestId == request_id);
        if (from.HasValue)
            query = query.Where(t => t.At >= from.Value);
        if (to.HasValue)
            query = query.Where(t => t.At <= to.Value);
        if (min_duration_ms is { } ms)
            query = query.Where(t => t.DurationMs != null && t.DurationMs >= ms);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();

            // Metadata is jsonb behind a ValueConverter, so LINQ cannot express
            // `metadata::text ILIKE …` over it. The matching ids are fetched once, in
            // raw SQL, and OR-ed into the predicate — capped, because a one-letter term
            // would otherwise pull the whole table into an IN list. Relational only:
            // the InMemory provider used by the tests has no SQL to send.
            var metaIds = _db.Database.IsNpgsql()
                ? await MetadataMatchesAsync(term, from, to, ct)
                : new List<Guid>();

            query = query.Where(t =>
                t.Action.ToLower().Contains(term)
                || t.Category.ToLower().Contains(term)
                || (t.ErrorMessage != null && t.ErrorMessage.ToLower().Contains(term))
                || (t.RequestId != null && t.RequestId.ToLower().Contains(term))
                || metaIds.Contains(t.TraceEventId));
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var who = user.Trim();
            if (Guid.TryParse(who, out var uid))
            {
                query = query.Where(t => t.UserId == uid);
            }
            else
            {
                var term = who.ToLower();
                var ids = _db.Users.AsNoTracking()
                    .Where(u => u.Username.ToLower().Contains(term) || u.Email.ToLower().Contains(term))
                    .Select(u => u.UserId);
                query = query.Where(t => t.UserId != null && ids.Contains(t.UserId.Value));
            }
        }

        var ordered = min_duration_ms is not null
            ? query.OrderByDescending(t => t.DurationMs).ThenByDescending(t => t.At)
            : query.OrderByDescending(t => t.At);

        var rows = await ordered
            .Skip(Math.Max(offset, 0))
            .Take(clamped)
            .ToListAsync(ct);

        return Ok(rows.Select(ToResponse));
    }

    // How many metadata hits a single search will carry into the IN list. Generous
    // enough that a real term never truncates, small enough that a stray one-letter
    // search stays one bounded query instead of a table-sized parameter.
    private const int MetadataMatchCap = 20_000;

    private async Task<List<Guid>> MetadataMatchesAsync(
        string term, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var lo = from ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var hi = to ?? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        // `position` rather than ILIKE: a plain substring test, with no metacharacters
        // to escape. The term is already lower-cased, and JSON metadata is full of the
        // % and _ that a LIKE pattern would have read as wildcards.
        return await _db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT "TraceEventId" AS "Value"
                 FROM trace_events
                 WHERE "IsActive" AND "At" >= {lo} AND "At" <= {hi}
                   AND position({term}::text in lower("Metadata"::text)) > 0
                 ORDER BY "At" DESC
                 LIMIT {MetadataMatchCap}
                 """)
            .ToListAsync(ct);
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
