using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Auth;

public class AuthAuditLogger : IAuthAuditLogger
{
    private readonly IAuthEventRepository _events;
    private readonly ILogger<AuthAuditLogger> _logger;

    public AuthAuditLogger(IAuthEventRepository events, ILogger<AuthAuditLogger> logger)
    {
        _events = events;
        _logger = logger;
    }

    public async Task LogAsync(
        AuthEventKind kind,
        Guid? userId,
        string ip,
        string userAgent,
        object? metadata = null,
        CancellationToken ct = default)
    {
        var metadataJson = metadata is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonSerializer.SerializeToElement(metadata);

        var entry = new AuthEvent
        {
            AuthEventId = Guid.NewGuid(),
            UserId = userId,
            Event = kind.ToWireString(),
            Ip = ip ?? string.Empty,
            UserAgent = userAgent ?? string.Empty,
            At = DateTime.UtcNow,
            Metadata = metadataJson,
        };

        try
        {
            await _events.AddAsync(entry, ct);
        }
        catch (Exception ex)
        {
            // Swallowing would hide a persistence bug, so we rethrow. Warn
            // here so the operator can correlate the DB failure with the
            // request that triggered it.
            _logger.LogWarning(ex,
                "auth.audit.write_failed event={Event} user_id={UserId}",
                entry.Event, userId);
            throw;
        }

        _logger.LogDebug(
            "auth.audit.write event={Event} user_id={UserId}",
            entry.Event, userId);
    }
}
