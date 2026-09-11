using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Controllers;

// Read-only list + detail + delete for the AI chat history.
//
// Scope is per user: a viewer only sees their own conversations, admins
// see every conversation. That's strict enough for v1 without needing a
// separate permission layer on top.
[ApiController]
[Route("api/ai/conversations")]
[HasPermission("conversations.read")]
public class AiConversationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;

    public AiConversationsController(AppDbContext db, ICurrentUser caller)
    {
        _db = db;
        _caller = caller;
    }

    // Returns the standard `{data, total, limit, offset}` envelope, like every
    // other list endpoint. `total` counts the caller's whole scoped history,
    // NOT the page: without it a client can only report how many rows it asked
    // for, which is how the /ai dashboard ended up permanently showing "5"
    // (its page size) as the conversation count.
    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<ListResponse<object>>> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, 200);
        var flooredOffset = Math.Max(offset, 0);
        var userIdStr = _caller.UserId.ToString();
        var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

        var query = _db.AIConversations.AsNoTracking()
            .Where(c => c.IsActive);
        if (!isAdmin)
            query = query.Where(c => c.UserId == userIdStr);

        // Counted off the same scoped query, before paging, so an admin's
        // total covers everyone and a user's covers only their own.
        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(c => c.UpdatedAt)
            .Skip(flooredOffset)
            .Take(clamped)
            .Select(c => new
            {
                c.AIConversationId,
                c.AgentId,
                c.UserId,
                c.Status,
                c.CreatedAt,
                c.UpdatedAt,
                c.Messages,
            })
            .ToListAsync(ct);

        return Ok(new ListResponse<object>
        {
            Data = rows.Select(r => (object)new
            {
                conversation_id = r.AIConversationId,
                agent_id = r.AgentId,
                user_id = r.UserId,
                status = r.Status,
                created_at = r.CreatedAt,
                updated_at = r.UpdatedAt,
                // First user message (truncated) makes a reasonable title in
                // the sidebar without the client having to replay history.
                title = DeriveTitle(r.Messages),
                message_count = CountMessages(r.Messages),
            }).ToList(),
            Total = total,
            Limit = clamped,
            Offset = flooredOffset,
        });
    }

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<object>> Get(Guid id, CancellationToken ct)
    {
        var row = await _db.AIConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AIConversationId == id
                                      && c.IsActive, ct);
        if (row is null) return NotFound();

        var userIdStr = _caller.UserId.ToString();
        var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));
        if (!isAdmin && row.UserId != userIdStr)
            return Forbid();

        // `Context` is deliberately NOT returned: the column has never been
        // populated by any code path (reserved in the initial schema, never
        // used), so surfacing it was a permanent `context: null` that read
        // like data loss. Re-add it here if it ever gains a writer.
        return Ok(new
        {
            conversation_id = row.AIConversationId,
            agent_id = row.AgentId,
            user_id = row.UserId,
            status = row.Status,
            // Rows created before the runner started stamping it carry null,
            // which has ALWAYS meant "web" (see the isExternal check in
            // AgentConversationRunner) — coalesce so the field never reads
            // as missing data.
            source = string.IsNullOrEmpty(row.Source) ? "web" : row.Source,
            messages = row.Messages,
            token_usage = row.TokenUsage,
            created_at = row.CreatedAt,
            updated_at = row.UpdatedAt,
        });
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.AIConversations
            .FirstOrDefaultAsync(c => c.AIConversationId == id
                                      && c.IsActive, ct);
        if (row is null) return NotFound();

        var userIdStr = _caller.UserId.ToString();
        var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));
        if (!isAdmin && row.UserId != userIdStr)
            return Forbid();

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Bulk delete — admins only. Soft-deletes every active conversation so
    // the chat sidebar can be cleared in one click without iterating the
    // whole list from the client.
    [HttpDelete]
    [EnableRateLimiting(RateLimitingConfiguration.AuthGeneric)]
    public async Task<ActionResult<object>> DeleteAll(CancellationToken ct)
    {
        var isAdmin = _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));
        if (!isAdmin) return Forbid();

        var now = DateTime.UtcNow;
        var affected = await _db.AIConversations
            .Where(c => c.IsActive)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.IsActive, false)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return Ok(new { deleted = affected });
    }

    // Pulls the first user message from the JSON blob. Keeps the query
    // side-by-side with pagination instead of shipping every conversation's
    // full messages array just to compute a title client-side.
    private static string DeriveTitle(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array) return "(new conversation)";
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (!m.TryGetProperty("role", out var r) || r.GetString() != "user") continue;
            if (!m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) continue;
            var text = c.GetString() ?? "";
            return text.Length > 80 ? text.Substring(0, 80) + "…" : text;
        }
        return "(new conversation)";
    }

    private static int CountMessages(JsonElement messages) =>
        messages.ValueKind == JsonValueKind.Array ? messages.GetArrayLength() : 0;
}
