using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using Microsoft.Extensions.Caching.Memory;

namespace flow_weaver_backend.Services.Settings;

// Singleton because the cache is shared across all requests; the row is
// read/written through IAppSettingsRepository resolved from a fresh scope per
// call so we don't leak a Scoped dependency into a Singleton.
public sealed class AppSettingsService : IAppSettingsService
{
    // 60-second TTL: a graduated rollout of the gating feature should propagate
    // within a minute of the admin flipping the toggle. Shorter would chatter
    // the DB; longer would surprise the admin.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private const string CacheKey = "app_settings";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    public AppSettingsService(IServiceScopeFactory scopeFactory, IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue<AppSettings>(CacheKey, out var cached) && cached is not null)
            return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        var row = await repo.GetAsync(tracking: false, ct);

        var resolved = row is null ? AppSettings.Default : ToSettings(row);
        _cache.Set(CacheKey, resolved, CacheTtl);
        return resolved;
    }

    public async Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        // The row is created on first write: a fresh deployment has no settings
        // until an admin saves the form, and defaults apply until then.
        var row = await repo.GetAsync(tracking: true, ct);
        if (row is null)
        {
            row = new AppSetting();
            repo.Add(row);
        }

        // Clamp out-of-range values so a malformed admin PUT can't poison the
        // matcher (threshold > 1.0 would block every fuzzy hit, gap < 0 would
        // auto-apply with no margin).
        row.PermissionsGranularGatingEnabled = updated.PermissionsGranularGatingEnabled;
        row.ImportFuzzyMatchThreshold = Math.Clamp(updated.ImportFuzzyMatchThreshold, 0.5, 0.99);
        row.ImportFuzzyMatchGap = Math.Clamp(updated.ImportFuzzyMatchGap, 0.0, 0.5);
        // Normalise so only the two known modes ever hit the column.
        row.RbacMode = Permission.RbacModes.Normalize(updated.RbacMode);
        row.UpdatedAt = DateTime.UtcNow;
        await repo.SaveChangesAsync(ct);

        var persisted = ToSettings(row);
        _cache.Set(CacheKey, persisted, CacheTtl);
        return persisted;
    }

    private static AppSettings ToSettings(AppSetting row) => new()
    {
        PermissionsGranularGatingEnabled = row.PermissionsGranularGatingEnabled,
        ImportFuzzyMatchThreshold = row.ImportFuzzyMatchThreshold,
        ImportFuzzyMatchGap = row.ImportFuzzyMatchGap,
        RbacMode = row.RbacMode,
    };
}
