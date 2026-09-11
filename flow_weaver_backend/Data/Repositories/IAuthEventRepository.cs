using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Append-only writer for AuthEvent rows (login/refresh/lockout audit). Like
// AuditEvent, AuthEvent has no soft-delete shape, so it stays off IRepository<T>.
public interface IAuthEventRepository
{
    Task AddAsync(AuthEvent entry, CancellationToken ct = default);
}
