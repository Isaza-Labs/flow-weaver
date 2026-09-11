using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Append-only writer for AuditEvent rows. AuditEvent has no IsActive /
// soft-delete shape, so it deliberately does NOT use IRepository<T> (whose
// soft-delete helpers don't apply) — it only stages + persists a row.
public interface IAuditEventRepository
{
    Task AddAsync(AuditEvent entry, CancellationToken ct = default);
}
