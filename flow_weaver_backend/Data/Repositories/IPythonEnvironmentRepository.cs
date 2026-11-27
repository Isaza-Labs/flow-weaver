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

    // ─── Install targets from the Packages allowlist ───────────────────

    // Pip specs of the ACTIVE pip allowlist rows that target each of these
    // environments (python_module_environments). Unmasked: callers mask
    // before echoing (responses, audit, errors). Every id is present in the
    // result, with an empty list when nothing targets it.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ListIncludedSpecsAsync(
        IReadOnlyCollection<Guid> environmentIds, CancellationToken ct = default);

    // The environment's own Requirements plus the specs above: what the
    // builder installs and hashes (PythonEnvironmentText.BuildEffectiveRequirements).
    Task<string> GetEffectiveRequirementsAsync(PythonEnvironment environment, CancellationToken ct = default);

    // Stages the removal of every module -> environment link of this
    // environment (an environment delete drops its targets instead of being
    // refused). Saved by the caller's SaveChangesAsync. Returns how many.
    Task<int> RemoveModuleLinksAsync(Guid environmentId, CancellationToken ct = default);

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
    // A change to the allowlist rows that target the environment (the other
    // half of its effective requirements) also puts it back to `pending`, so
    // the Status condition covers that too; the builder additionally
    // re-compares the effective requirements before it swaps the build in
    // (PythonEnvironmentBuildGuard). False when the condition no longer holds.
    Task<bool> MarkReadyAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string resolvedPackages, string requirementsHash, CancellationToken ct = default);

    Task<bool> MarkFailedAsync(
        Guid id, string claimedRequirements, string? claimedPythonVersion,
        string error, CancellationToken ct = default);
}
