using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public interface IPythonEnvironmentRepository : IRepository<PythonEnvironment>
{
    // Active row with this exact name, for the create/rename uniqueness check.
    Task<PythonEnvironment?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // The active SSH-fallback environment (at most one), optionally ignoring
    // one id — the row being edited.
    Task<PythonEnvironment?> FindActiveSshFallbackAsync(Guid? excludeId = null, CancellationToken ct = default);

    // Active devices and snippets that still point at this environment —
    // a delete would leave them failing at run time.
    Task<(int Devices, int Snippets)> CountReferencesAsync(Guid id, CancellationToken ct = default);

    // ─── Builder (runs on the worker) ──────────────────────────────────

    // Atomically claims one environment that needs building — a `pending`
    // row, or a `building` row whose claim went stale (a builder that died
    // mid-build). Flips it to `building` with a single conditional UPDATE, so
    // only one process wins each row. Null when there's nothing to do.
    Task<PythonEnvironment?> ClaimNextForBuildAsync(DateTime staleBefore, CancellationToken ct = default);

    // Records a finished build. Conditional on the row still being `building`
    // with the SAME requirements + python version the builder claimed: an
    // admin edit during the build already put the row back to `pending`, and
    // this write must not flip that back to `ready` for the old definition.
    // False when the condition no longer holds.
    Task<bool> MarkReadyAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string resolvedPackages, string requirementsHash, CancellationToken ct = default);

    Task<bool> MarkFailedAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string error, CancellationToken ct = default);
}
