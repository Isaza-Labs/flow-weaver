namespace flow_weaver_backend.Services.Audit;

/// <summary>
/// Append-only write surface for the domain audit trail. Each call persists
/// one <see cref="flow_weaver_backend.Models.AuditEvent"/> row capturing who
/// mutated what and when.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        string entityType,
        Guid? entityId,
        string action,
        object? before = null,
        object? after = null,
        CancellationToken ct = default);
}
