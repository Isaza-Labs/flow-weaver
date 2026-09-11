using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// The settings singleton has an int primary key and no soft-delete column, so
// none of the generic IRepository<T> helpers fit. It exposes just the two
// operations the settings service needs.
public interface IAppSettingsRepository
{
    // The settings row, or null before the first write. tracking:true when the
    // caller will mutate + save.
    Task<AppSetting?> GetAsync(bool tracking = false, CancellationToken ct = default);

    void Add(AppSetting row);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
