using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// StepRun reads. Steps are listed by their parent run ordered oldest-first
// (timeline order), which the generic newest-first ListAsync can't express.
public interface IStepRunRepository : IRepository<StepRun>
{
    Task<IReadOnlyList<StepRun>> ListByRunAsync(
        Guid runId, int limit, int offset, CancellationToken ct = default);

    Task<int> CountByRunAsync(Guid runId, CancellationToken ct = default);

    // Every step of one run, oldest-first, with NO IsActive
    // filter (mirrors get_run_details, which lists steps regardless of
    // soft-delete state). Returns full entities; the caller projects.
    Task<IReadOnlyList<StepRun>> ListByRunUnfilteredAsync(
        Guid runId, CancellationToken ct = default);

    // Active step outputs across a set of runs, projected to
    // (run, node, status, output). Backs the acceptance-test grader's
    // single-query pre-fetch over candidate runs. Read-only.
    Task<IReadOnlyList<StepOutputProjection>> ListOutputsByRunIdsAsync(
        IReadOnlyCollection<Guid> runIds, CancellationToken ct = default);

    // How many times each of `snippetIds` has completed successfully, and when
    // it last did. Snippets with no successful step are absent from the map
    // (the caller reads that as zero) rather than present with a 0 — one row
    // per snippet that has actually run keeps the result small.
    //
    // Backs the "unproven" split in the workflow editor palette: a snippet
    // nobody ever got to complete is a draft, not a reusable building block.
    //
    // Deliberately NOT filtered by StepRun.IsActive. Deleting a run is
    // housekeeping on the run log; it is not a statement that the snippet never
    // worked. Filtering would let a cleanup sweep silently demote a proven
    // snippet back to "unproven" months later.
    Task<IReadOnlyDictionary<Guid, SnippetRunStats>> GetCompletedStatsBySnippetAsync(
        IReadOnlyCollection<Guid> snippetIds, CancellationToken ct = default);
}

// Successful-execution tally for one snippet. `LastCompletedAt` is null only
// when the row is absent from the map entirely, so a present record always
// means CompletedRuns >= 1.
public sealed record SnippetRunStats(int CompletedRuns, DateTime? LastCompletedAt);

// (run, node, status, output) projection for the acceptance-test grader.
public sealed record StepOutputProjection(Guid RunId, string NodeId, string Status, JsonElement Output);
