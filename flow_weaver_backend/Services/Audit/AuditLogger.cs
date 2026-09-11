using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Security;

namespace flow_weaver_backend.Services.Audit;

/// <summary>
/// Scoped service that writes <see cref="AuditEvent"/> rows. Resolves the
/// current user, HTTP context metadata (IP, User-Agent, request ID) and
/// serialises before/after snapshots to <see cref="JsonElement"/> for storage
/// in a jsonb column.
/// </summary>
public class AuditLogger : IAuditLogger
{
    // Snapshots come from anonymous audit projections whose JsonElement
    // members may be Undefined (`dto.X ?? default` in service mappers).
    // The HTTP response pipeline already neutralises that via
    // SafeJsonElementConverter in AddJsonOptions; use the same converter
    // here so audit serialisation can't 500 a committed mutation.
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        Converters = { new SafeJsonElementConverter() },
    };

    private readonly IAuditEventRepository _events;
    private readonly ICurrentUser _caller;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(
        IAuditEventRepository events,
        ICurrentUser caller,
        IHttpContextAccessor http,
        ILogger<AuditLogger> logger)
    {
        _events = events;
        _caller = caller;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(
        string entityType,
        Guid? entityId,
        string action,
        object? before = null,
        object? after = null,
        CancellationToken ct = default)
    {
        var ctx = _http.HttpContext;

        var beforeJson = SerializeSnapshot(before, "before", entityType, entityId, action);
        var afterJson = SerializeSnapshot(after, "after", entityType, entityId, action);

        var authenticated = _caller.IsAuthenticated;
        Guid? userId = authenticated ? _caller.UserId : null;
        var actor = authenticated ? _caller.Username : null;

        // UserId stays null for automation; Actor is what tells "workflow-runner"
        // apart from "git-webhook" after the fact.
        userId = userId == Guid.Empty ? null : userId;

        var entry = new AuditEvent
        {
            AuditEventId = Guid.NewGuid(),
            UserId = userId,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Ip = ClientIp.Resolve(ctx),
            UserAgent = ctx?.Request.Headers.UserAgent.ToString(),
            At = DateTime.UtcNow,
            RequestId = CorrelationMiddleware.CurrentRequestId(ctx),
        };

        try
        {
            await _events.AddAsync(entry, ct);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, matching ITraceLogger's policy.
            //
            // Every caller writes the audit row AFTER committing the mutation,
            // so rethrowing here answered a request that had already taken
            // effect with a 500 — the client retries, the change is applied
            // twice, and the audit trail is still missing the first one.
            // Losing the row is bad; lying about whether the change landed is
            // worse. `audit.write.failed` is logged at Error precisely so it
            // can be alerted on: it means the trail has a hole.
            _logger.LogError(ex,
                "audit.write.failed entity_type={EntityType} entity_id={EntityId} action={Action} "
                + " actor={Actor} request_id={RequestId}",
                entityType, entityId, action, entry.Actor, entry.RequestId);
            return;
        }

        _logger.LogDebug(
            "audit.write.ok entity_type={EntityType} entity_id={EntityId} action={Action} "
            + " user_id={UserId} actor={Actor}",
            entityType, entityId, action, userId, entry.Actor);
    }

    /// <summary>
    /// Serialises a snapshot, degrading to JSON null instead of throwing —
    /// same policy as the write itself: a broken snapshot (e.g. an
    /// Undefined JsonElement inside the projection) must not turn an
    /// already-committed mutation into a 500. The row is still written so
    /// the trail keeps the who/what/when even when the payload is lost.
    /// </summary>
    private JsonElement SerializeSnapshot(
        object? value, string side, string entityType, Guid? entityId, string action)
    {
        if (value is null)
            return JsonDocument.Parse("null").RootElement;

        try
        {
            return JsonSerializer.SerializeToElement(value, SnapshotOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "audit.snapshot.serialize_failed side={Side} entity_type={EntityType} "
                + "entity_id={EntityId} action={Action}",
                side, entityType, entityId, action);
            return JsonDocument.Parse("null").RootElement;
        }
    }
}
