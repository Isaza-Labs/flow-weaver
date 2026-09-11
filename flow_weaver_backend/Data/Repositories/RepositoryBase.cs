using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

// EF Core implementation of IRepository<TEntity>. Lives in the host because it
// depends on AppDbContext; the service layer only sees IRepository<T> and the
// entity-specific interfaces that extend it.
//
// The soft-delete filter is expressed with EF.Property<>(...) so a single
// implementation serves every entity derived from BaseModel, which carries
// IsActive + CreatedAt. Entities WITHOUT those columns (AuthEvent,
// RefreshToken, AuditEvent, AppSetting) must NOT rely on the generic
// defaults: either give them a specific repository that overrides Query /
// ListAsync, or call with activeOnly: false plus a custom order. The query
// methods are virtual for exactly that; entities with extra persistence
// rules override SaveChangesAsync (see DeviceRepository).
public class RepositoryBase<TEntity> : IRepository<TEntity> where TEntity : class
{
    // Single-column Guid PK name (e.g. "DeviceId") resolved once per closed
    // generic type from EF metadata, so subclasses never hardcode it. The race
    // on first access is benign: every writer computes the same string.
    private static string? _primaryKeyName;

    protected readonly AppDbContext Db;
    protected readonly DbSet<TEntity> Set;

    public RepositoryBase(AppDbContext db)
    {
        Db = db;
        Set = db.Set<TEntity>();
    }

    private string PrimaryKeyName =>
        _primaryKeyName ??=
            Db.Model.FindEntityType(typeof(TEntity))!.FindPrimaryKey()!.Properties[0].Name;

    // Base query with the optional soft-delete filter applied.
    protected IQueryable<TEntity> Query(bool activeOnly, bool tracking)
    {
        IQueryable<TEntity> q = tracking ? Set : Set.AsNoTracking();
        if (activeOnly)
            q = q.Where(e => EF.Property<bool>(e, nameof(BaseModel.IsActive)));
        return q;
    }

    public virtual async Task<TEntity?> GetByIdAsync(
        Guid id, bool activeOnly = true, bool tracking = true,
        CancellationToken ct = default)
    {
        var pk = PrimaryKeyName;
        return await Query(activeOnly, tracking)
            .FirstOrDefaultAsync(e => EF.Property<Guid>(e, pk) == id, ct);
    }

    public virtual async Task<IReadOnlyList<TEntity>> ListAsync(
        int limit, int offset, bool activeOnly = true,
        CancellationToken ct = default)
        => await Query(activeOnly, tracking: false)
            .OrderByDescending(e => EF.Property<DateTime>(e, nameof(BaseModel.CreatedAt)))
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public virtual async Task<int> CountAsync(
        bool activeOnly = true, CancellationToken ct = default)
        => await Query(activeOnly, tracking: false).CountAsync(ct);

    public virtual async Task<bool> ExistsAsync(
        Guid id, bool activeOnly = true, CancellationToken ct = default)
    {
        var pk = PrimaryKeyName;
        return await Query(activeOnly, tracking: false)
            .AnyAsync(e => EF.Property<Guid>(e, pk) == id, ct);
    }

    public virtual async Task<IReadOnlyList<TEntity>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids, bool activeOnly = true,
        CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<TEntity>();
        var pk = PrimaryKeyName;
        return await Query(activeOnly, tracking: false)
            .Where(e => ids.Contains(EF.Property<Guid>(e, pk)))
            .ToListAsync(ct);
    }

    public virtual void Add(TEntity entity) => Set.Add(entity);

    // Marks the WHOLE entity Modified (every column written on save). Use only
    // for detached upserts. The codebase convention — and the cheaper, less
    // concurrency-prone path — is to mutate the tracked entity returned by
    // GetByIdAsync(tracking: true) and just call SaveChangesAsync.
    public virtual void Update(TEntity entity) => Set.Update(entity);

    public virtual void Remove(TEntity entity) => Set.Remove(entity);

    public virtual Task<int> SaveChangesAsync(CancellationToken ct = default)
        => Db.SaveChangesAsync(ct);
}
