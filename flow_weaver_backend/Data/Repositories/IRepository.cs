namespace flow_weaver_backend.Data.Repositories;

// Generic data-access contract that hides AppDbContext / EF Core from the
// service layer. This is the seam the modularization plan calls for: services
// depend on IRepository<T> (and the entity-specific interfaces that extend it)
// instead of talking to AppDbContext directly.
//
// Commands (Add/Update/Remove) only stage changes in the unit of work; call
// SaveChangesAsync to persist. Implementations translate persistence faults
// (e.g. unique-constraint violations) into domain exceptions so callers never
// see EF Core or Npgsql types.
public interface IRepository<TEntity> where TEntity : class
{
    // Single entity by primary key. `activeOnly` applies the IsActive
    // soft-delete filter (entities without that column must pass
    // activeOnly: false). `tracking: false` returns a read-only (AsNoTracking)
    // instance — use it for pure reads, leave it true when the caller mutates
    // and saves the returned entity.
    Task<TEntity?> GetByIdAsync(
        Guid id,
        bool activeOnly = true, bool tracking = true,
        CancellationToken ct = default);

    // Paginated list, newest first (CreatedAt DESC). Bounds are the
    // caller's responsibility (see Services.Common.Pagination.Clamp).
    Task<IReadOnlyList<TEntity>> ListAsync(
        int limit, int offset,
        bool activeOnly = true, CancellationToken ct = default);

    // COUNT(*) over the same filter ListAsync applies.
    Task<int> CountAsync(
        bool activeOnly = true, CancellationToken ct = default);

    // True when an entity with this id exists. Use for parent
    // foreign-key existence checks — cheaper than GetByIdAsync (no entity is
    // materialised, it compiles to SELECT 1 ... EXISTS).
    Task<bool> ExistsAsync(
        Guid id, bool activeOnly = true, CancellationToken ct = default);

    // Batch fetch by primary key (e.g. resolve every snippet
    // a workflow references in one round-trip). Order is unspecified; callers
    // that need a lookup build their own dictionary from the result.
    Task<IReadOnlyList<TEntity>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        bool activeOnly = true, CancellationToken ct = default);

    void Add(TEntity entity);
    void Update(TEntity entity);
    void Remove(TEntity entity);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
