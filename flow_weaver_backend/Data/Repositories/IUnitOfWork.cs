namespace flow_weaver_backend.Data.Repositories;

// Commits the work staged on the shared (scoped) AppDbContext. Inject this in
// services that mutate more than one aggregate in a single request so the
// intent ("commit the unit of work") is explicit instead of piggy-backing on
// one entity's repository SaveChangesAsync. Single-entity services keep using
// IRepository<T>.SaveChangesAsync.
//
// Every repository and the unit of work resolve the same scoped DbContext, so
// one SaveChanges flushes every staged change across all of them in a single
// transaction.
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    // Runs `work` inside an explicit DB transaction: commits on success, rolls
    // back and rethrows on any exception. Use for multi-statement units that
    // want all-or-nothing semantics spelled out (e.g. the integration bundle,
    // which stages an integration + its skills/specs/actions and commits them
    // atomically).
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}
