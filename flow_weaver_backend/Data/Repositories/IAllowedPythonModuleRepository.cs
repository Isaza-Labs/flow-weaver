using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public interface IAllowedPythonModuleRepository : IRepository<AllowedPythonModule>
{
    // Exact (case-sensitive — Python import names are) lookup, for the
    // create-time uniqueness check.
    Task<AllowedPythonModule?> FindByImportNameAsync(
        string importName, CancellationToken ct = default);

    // Import names of every READY module — the set the PythonHandler merges
    // into its static safe-list at execution time.
    Task<IReadOnlyList<string>> ListReadyImportNamesAsync(
        CancellationToken ct = default);

    // ─── Provisioner (runs on the worker) ──────────────────────────────

    // Atomically claims one pip module that needs installing — a `pending`
    // row, or an `installing` row whose claim went stale (a provisioner that
    // crashed mid-install). Flips it to `installing` and returns it, or null
    // when there's nothing to do. Safe to call from multiple processes: the
    // status flip is a single conditional UPDATE, so only one wins each row.
    Task<AllowedPythonModule?> ClaimNextForProvisionAsync(
        DateTime staleBefore, CancellationToken ct = default);

    // Mark a claimed row installed (ready, with the resolved version) or failed
    // (with the error). Keyed by id; no-op if the row vanished (deleted meanwhile).
    Task MarkReadyAsync(Guid id, string? installedVersion, CancellationToken ct = default);

    // Points a claimed row at the import name its installed distribution actually
    // provides. False when another row already owns that name — two approvals for
    // one module is a state to refuse, not to merge. Called while the row is
    // `installing`, so no snippet can have seen the interim name: only `ready` rows
    // reach the handler.
    Task<bool> RenameImportNameAsync(Guid id, string importName, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);
}
