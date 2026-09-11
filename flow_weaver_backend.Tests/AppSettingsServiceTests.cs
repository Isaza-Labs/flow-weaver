using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace flow_weaver_backend.Tests;

// The service is small enough that round-trip coverage (seed row → read →
// flip via Update → re-read) is more useful than mocking each dependency.
// The cache invariants — cache returns warm values, Update overwrites the
// cache — are what we actually care about, since the rest is EF passthrough.
public class AppSettingsServiceTests
{
    private static (IServiceProvider sp, AppDbContext db) NewProvider(string name)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddMemoryCache();
        services.AddScoped<IAppSettingsRepository, AppSettingsRepository>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<AppDbContext>());
    }

    private static void SeedSettings(AppDbContext db, bool gating)
    {
        db.AppSettings.Add(new AppSetting { PermissionsGranularGatingEnabled = gating });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetAsync_returns_default_when_row_missing()
    {
        var (sp, _) = NewProvider(nameof(GetAsync_returns_default_when_row_missing));
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var result = await svc.GetAsync();

        Assert.False(result.PermissionsGranularGatingEnabled);
        Assert.Equal("legacy", result.RbacMode);
    }

    [Fact]
    public async Task GetAsync_reads_persisted_value()
    {
        var (sp, db) = NewProvider(nameof(GetAsync_reads_persisted_value));
        SeedSettings(db, gating: true);
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var result = await svc.GetAsync();

        Assert.True(result.PermissionsGranularGatingEnabled);
    }

    [Fact]
    public async Task GetAsync_caches_value_across_calls()
    {
        var (sp, db) = NewProvider(nameof(GetAsync_caches_value_across_calls));
        SeedSettings(db, gating: false);
        var svc = sp.GetRequiredService<IAppSettingsService>();

        // Warm the cache.
        var first = await svc.GetAsync();
        Assert.False(first.PermissionsGranularGatingEnabled);

        // Mutate the row out-of-band — the cache should still serve the
        // stale value because nothing called UpdateAsync to invalidate it.
        var row = db.AppSettings.Single(s => s.Id == AppSetting.SingletonId);
        row.PermissionsGranularGatingEnabled = true;
        await db.SaveChangesAsync();

        var second = await svc.GetAsync();
        Assert.False(second.PermissionsGranularGatingEnabled);
    }

    [Fact]
    public async Task UpdateAsync_persists_and_refreshes_cache()
    {
        var (sp, db) = NewProvider(nameof(UpdateAsync_persists_and_refreshes_cache));
        SeedSettings(db, gating: false);
        var svc = sp.GetRequiredService<IAppSettingsService>();

        // Warm the cache with the false value.
        _ = await svc.GetAsync();

        var updated = await svc.UpdateAsync(new AppSettings
        {
            PermissionsGranularGatingEnabled = true,
        });

        Assert.True(updated.PermissionsGranularGatingEnabled);

        // The cache entry should now reflect the new value, not the stale
        // false the first GetAsync inserted.
        var subsequent = await svc.GetAsync();
        Assert.True(subsequent.PermissionsGranularGatingEnabled);

        // And the DB should match.
        var row = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == AppSetting.SingletonId);
        Assert.True(row.PermissionsGranularGatingEnabled);
    }

    [Fact]
    public async Task UpdateAsync_clamps_fuzzy_thresholds_to_safe_ranges()
    {
        var (sp, db) = NewProvider(nameof(UpdateAsync_clamps_fuzzy_thresholds_to_safe_ranges));
        SeedSettings(db, gating: false);
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var updated = await svc.UpdateAsync(new AppSettings
        {
            PermissionsGranularGatingEnabled = false,
            ImportFuzzyMatchThreshold = 1.5,  // out of range high → clamp to 0.99
            ImportFuzzyMatchGap = -0.2,       // out of range low → clamp to 0.0
        });

        Assert.Equal(0.99, updated.ImportFuzzyMatchThreshold);
        Assert.Equal(0.0, updated.ImportFuzzyMatchGap);

        var row = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == AppSetting.SingletonId);
        Assert.Equal(0.99, row.ImportFuzzyMatchThreshold);
        Assert.Equal(0.0, row.ImportFuzzyMatchGap);
    }

    [Fact]
    public async Task GetAsync_returns_fuzzy_threshold_defaults_for_a_fresh_install()
    {
        var (sp, db) = NewProvider(nameof(GetAsync_returns_fuzzy_threshold_defaults_for_a_fresh_install));
        SeedSettings(db, gating: false);
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var result = await svc.GetAsync();

        Assert.Equal(0.8, result.ImportFuzzyMatchThreshold);
        Assert.Equal(0.1, result.ImportFuzzyMatchGap);
    }

    [Fact]
    public async Task UpdateAsync_creates_the_singleton_row_when_missing()
    {
        var (sp, db) = NewProvider(nameof(UpdateAsync_creates_the_singleton_row_when_missing));
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var updated = await svc.UpdateAsync(new AppSettings { PermissionsGranularGatingEnabled = true });

        Assert.True(updated.PermissionsGranularGatingEnabled);
        var row = await db.AppSettings.AsNoTracking().SingleAsync(s => s.Id == AppSetting.SingletonId);
        Assert.True(row.PermissionsGranularGatingEnabled);
    }

    [Fact]
    public async Task UpdateAsync_normalises_an_unknown_rbac_mode_to_legacy()
    {
        var (sp, _) = NewProvider(nameof(UpdateAsync_normalises_an_unknown_rbac_mode_to_legacy));
        var svc = sp.GetRequiredService<IAppSettingsService>();

        var updated = await svc.UpdateAsync(new AppSettings { RbacMode = "not-a-mode" });

        Assert.Equal("legacy", updated.RbacMode);
    }
}
