namespace flow_weaver_backend.Services.Settings;

// Application-wide feature flags + operational settings. Reads are cached so
// hot-path call sites (every workflow update / integration update) don't pay a
// round-trip per request. The cache is invalidated on every write.
public sealed record AppSettings
{
    // When true, mutations on workflows / integrations consult
    // IResourcePermissionService.HasAtLeastAsync. Default false keeps the
    // historic behaviour (global Admin/Operator/Viewer wins).
    public bool PermissionsGranularGatingEnabled { get; init; }

    // Thresholds the import wizard's fuzzy action-name matcher consults when
    // the exact name match fails. The defaults (0.8 / 0.1) are calibrated for
    // typical Itential / n8n catalogues; admins can dial them if their action
    // names are unusually similar (lower threshold) or unusually
    // mission-critical (higher).
    public double ImportFuzzyMatchThreshold { get; init; } = 0.8;
    public double ImportFuzzyMatchGap { get; init; } = 0.1;

    // "legacy" | "granular" — RBAC-granular rollout switch (see RbacModes).
    // Default "legacy" preserves the Admin/Operator/Viewer behaviour.
    public string RbacMode { get; init; } = "legacy";

    public static AppSettings Default => new();
}

public interface IAppSettingsService
{
    // Current settings. Hits the cache when warm; otherwise queries the
    // singleton row and caches the result.
    Task<AppSettings> GetAsync(CancellationToken ct = default);

    // Writes new values to the singleton row (creating it on first write) and
    // refreshes the cache so the next GetAsync reads fresh data. Returns the
    // persisted settings (echoed for chaining).
    Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default);
}
