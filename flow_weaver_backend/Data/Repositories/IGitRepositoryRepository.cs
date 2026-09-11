using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Git repository registrations. Adds the name-ordered listing and the
// duplicate-name guard GitService needs; generic CRUD (GetById/Add/Save)
// comes from the base.
public interface IGitRepositoryRepository : IRepository<GitRepository>
{
    // Active repositories ordered by Name (not CreatedAt), paginated.
    // Read-only.
    Task<IReadOnlyList<GitRepository>> ListOrderedByNameAsync(
        int limit, int offset, CancellationToken ct = default);

    // True when an active repository with this exact name already exists.
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    // Active repository with this exact name, case-insensitive — the portable
    // identity a workflow bundle's `repository` key (and a git step's
    // `repository` alias) resolves against. Exact, never fuzzy.
    Task<GitRepository?> FindActiveByNameAsync(string name, CancellationToken ct = default);
}
