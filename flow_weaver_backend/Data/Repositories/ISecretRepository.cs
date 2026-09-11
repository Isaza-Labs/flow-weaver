using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Secret reads for the resolver. Generic CRUD resolves via the open-generic
// IRepository<Secret>; this adds the id-or-exact-name lookup the
// ${secret:secret:...} resolver uses.
public interface ISecretRepository : IRepository<Secret>
{
    // Active secret resolved by id (isGuid) or exact Name.
    Task<Secret?> FindActiveByIdOrNameAsync(
        Guid id, string name, bool isGuid, CancellationToken ct = default);
}
