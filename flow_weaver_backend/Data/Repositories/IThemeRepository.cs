using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

public interface IThemeRepository : IRepository<Theme>
{
    // Everything the given user may see in the picker: published themes plus
    // their own private ones. Newest first, shared before private so the
    // org's themes lead the list.
    Task<IReadOnlyList<Theme>> ListVisibleToAsync(
        Guid userId, int limit, int offset, CancellationToken ct = default);

    Task<int> CountVisibleToAsync(Guid userId, CancellationToken ct = default);

    // Case-insensitive name check within one visibility scope — two users may
    // each have a private "Midnight", but the shared list holds one.
    Task<Theme?> FindByNameAsync(
        string name, bool isShared, Guid? ownerUserId, CancellationToken ct = default);
}
