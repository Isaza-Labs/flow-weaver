using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Snippet persistence with the by-name lookup the plan builder needs to reuse
// an existing snippet instead of minting a duplicate. Generic CRUD (used by
// SnippetService) still resolves via the open-generic IRepository<Snippet>.
public interface ISnippetRepository : IRepository<Snippet>
{
    // First active snippet whose Name matches exactly, case-insensitively,
    // after trimming (bundle/SPEC.md 5.1). Read-only: a bundle import resolves
    // against this row and must never attach it to the change tracker.
    Task<Snippet?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // See IIntegrationRepository — same contract, same reasons.
    Task<HashSet<string>> ListTakenSlugsAsync(CancellationToken ct = default);

    // Active snippet with this slug, matched trimmed + case-insensitively.
    // Read-only.
    Task<Snippet?> FindActiveBySlugAsync(string slug, CancellationToken ct = default);

    // Every active snippet, projected to id/name/type (no Code or
    // schemas). Backs the import dependency resolver's name/type matching.
    Task<IReadOnlyList<SnippetSummary>> ListActiveSummariesAsync(
        CancellationToken ct = default);

    // Active snippets, optionally filtered by type, ordered by
    // Name ASC, capped at `limit`, projected to the catalog row the
    // list_snippets tool returns. Read-only.
    Task<IReadOnlyList<SnippetCatalogRow>> ListActiveCatalogAsync(
        string? type, int limit, CancellationToken ct = default);

    // Active snippets whose id is in `ids`, projected to the target/required-key
    // fields the prompt-sufficiency evaluator inspects. Read-only.
    Task<IReadOnlyList<SnippetIntakeRow>> ListActiveIntakeByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    // Of the given ids, the subset that maps to an active snippet. Backs
    // the simulator's snippet-existence check. Read-only.
    Task<HashSet<Guid>> ExistingActiveIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

// Minimal snippet shape for name/type matching during import analysis.
public sealed record SnippetSummary(Guid SnippetId, string Name, string Type);

// list_snippets catalog row. `Proven` = at least one step using this snippet has
// reached `completed`, the same signal the editor palette uses to separate
// reusable blocks from drafts. The agent sees it so it stops proposing a
// half-finished experiment when a working equivalent exists.
public sealed record SnippetCatalogRow(
    Guid SnippetId, string Name, string Type, string? Description, bool Proven);

// Fields evaluate_prompt_sufficiency reads to derive target + required-key
// clarifying questions.
public sealed record SnippetIntakeRow(
    Guid SnippetId, string Name, string Type, string TargetMode, System.Text.Json.JsonElement InputSchema);
