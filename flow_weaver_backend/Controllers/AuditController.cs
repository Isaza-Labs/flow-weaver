using System.Security.Claims;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuditController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns domain audit events. Supports filtering
    /// by entity type, user, action, and time window. Admin-only.
    /// </summary>
    [HttpGet("events")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<IEnumerable<AuditEventResponse>>> Events(
        // All filters default to null so callers (and tests) can name just the
        // one they care about instead of threading positional nulls through a
        // signature that grows every time a filter is added.
        [FromQuery] string? entity_type = null,
        [FromQuery] Guid? entity_id = null,
        [FromQuery] Guid? user_id = null,
        [FromQuery] string? actor = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? action = null,
        [FromQuery] string? action_prefix = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {

        var query = _db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(entity_type))
            query = query.Where(e => e.EntityType == entity_type);
        // "Show me the history of this one workflow / credential / user."
        // EntityId was stored from the start but had no filter, so the
        // single most natural audit question had no answer through the API.
        if (entity_id.HasValue)
            query = query.Where(e => e.EntityId == entity_id.Value);
        if (user_id.HasValue)
            query = query.Where(e => e.UserId == user_id.Value);
        // Automation has no UserId — filtering by actor is the only way to
        // ask "what did the scheduled runs change last night".
        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(e => e.Actor == actor);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(e => e.Action == action);
        // action_prefix lets the UI surface a category (e.g. all variants of
        // "allow_private_network.*") without requiring exact-match matrix.
        if (!string.IsNullOrWhiteSpace(action_prefix))
            query = query.Where(e => EF.Functions.Like(e.Action, action_prefix + "%"));
        if (from.HasValue)
            query = query.Where(e => e.At >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.At <= to.Value);

        var events = await query
            .OrderByDescending(e => e.At)
            .Skip(Math.Max(offset, 0))
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return Ok(events.Select(e => new AuditEventResponse
        {
            AuditEventId = e.AuditEventId,
            UserId = e.UserId,
            Actor = e.Actor,
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            Action = e.Action,
            BeforeJson = e.BeforeJson,
            AfterJson = e.AfterJson,
            Ip = e.Ip,
            UserAgent = e.UserAgent,
            At = e.At,
            RequestId = e.RequestId,
        }));
    }
}
