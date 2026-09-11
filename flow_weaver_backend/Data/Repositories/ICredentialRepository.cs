using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Credential reads for the secret resolver. Generic CRUD (and the device/SSH
// handlers) resolve via the open-generic IRepository<Credential>; this adds the
// id-or-exact-name lookup the ${secret:credential:...} resolver uses.
public interface ICredentialRepository : IRepository<Credential>
{
    // Active credential resolved by id (isGuid) or exact Name.
    Task<Credential?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default);

    // Active credential with this exact name, case-insensitive — the portable
    // identity a workflow bundle's `credential` key resolves against
    // (bundle/SPEC.md §5.1). Exact, never fuzzy.
    Task<Credential?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // Active credentials filtered by optional type / auth method
    // / case-insensitive name substring, ordered by Name, capped at `limit`,
    // projected to METADATA ONLY (never secret material). Backs list_credentials.
    Task<IReadOnlyList<CredentialListRow>> ListMetadataAsync(
        string? type, string? authMethod, string? nameContains,
        int limit, CancellationToken ct = default);
}

// list_credentials metadata row — deliberately carries no secret material.
public sealed record CredentialListRow(
    Guid CredentialId, string Name, string Type, string? Username,
    string AuthMethod, bool HasPrivateKey, DateTime CreatedAt, DateTime UpdatedAt);
