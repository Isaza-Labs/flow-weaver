using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Application-wide operational settings. Currently exposes a single toggle —
// the granular RBAC overlay — but the shape is intentionally an object
// so future app-wide flags can land here without a new controller
// or a breaking change to the response schema.
[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = "Admin")]
public class AdminSettingsController : ControllerBase
{
    private readonly IAppSettingsService _settings;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AdminSettingsController> _logger;

    public AdminSettingsController(
        IAppSettingsService settings,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<AdminSettingsController> logger)
    {
        _settings = settings;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public async Task<ActionResult<AppSettingsResponse>> Get(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        return Ok(ToResponse(s));
    }

    [HttpPut]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AppSettingsResponse>> Put(
        [FromBody] AppSettingsRequest body,
        CancellationToken ct)
    {
        var before = await _settings.GetAsync(ct);
        var desired = new AppSettings
        {
            PermissionsGranularGatingEnabled = body.PermissionsGranularGatingEnabled,
            ImportFuzzyMatchThreshold = body.ImportFuzzyMatchThreshold,
            ImportFuzzyMatchGap = body.ImportFuzzyMatchGap,
            RbacMode = body.RbacMode,
        };
        var after = await _settings.UpdateAsync(desired, ct);

        // RBAC-granular rollout switch — audit-worthy: it changes how every
        // migrated endpoint authorizes.
        if (!string.Equals(before.RbacMode, after.RbacMode, StringComparison.Ordinal))
        {
            await _audit.LogAsync("app_settings", null, "app_settings.rbac_mode.changed",
                before: new { rbac_mode = before.RbacMode },
                after: new { rbac_mode = after.RbacMode });
            _logger.LogInformation(
                "app_settings.rbac_mode.changed user_id={UserId} new_value={NewValue}",
                _caller.UserId, after.RbacMode);
        }

        // Only emit the audit row when something actually changed so an
        // admin re-saving the form unchanged doesn't pollute /admin/audit.
        if (before.PermissionsGranularGatingEnabled != after.PermissionsGranularGatingEnabled)
        {
            var action = after.PermissionsGranularGatingEnabled
                ? "app_settings.granular_gating.enabled"
                : "app_settings.granular_gating.disabled";
            await _audit.LogAsync("app_settings", null, action,
                before: new { permissions_granular_gating_enabled = before.PermissionsGranularGatingEnabled },
                after: new { permissions_granular_gating_enabled = after.PermissionsGranularGatingEnabled });
            _logger.LogInformation(
                "app_settings.granular_gating.toggled user_id={UserId} new_value={NewValue}",
                _caller.UserId, after.PermissionsGranularGatingEnabled);
        }
        // Less critical for audit but still worth recording when an
        // admin tightens or loosens the matcher — helps debugging
        // future "why did the import auto-resolve action X" tickets.
        if (Math.Abs(before.ImportFuzzyMatchThreshold - after.ImportFuzzyMatchThreshold) > 1e-9
            || Math.Abs(before.ImportFuzzyMatchGap - after.ImportFuzzyMatchGap) > 1e-9)
        {
            await _audit.LogAsync("app_settings", null,
                "app_settings.import_fuzzy_match.updated",
                before: new
                {
                    import_fuzzy_match_threshold = before.ImportFuzzyMatchThreshold,
                    import_fuzzy_match_gap = before.ImportFuzzyMatchGap,
                },
                after: new
                {
                    import_fuzzy_match_threshold = after.ImportFuzzyMatchThreshold,
                    import_fuzzy_match_gap = after.ImportFuzzyMatchGap,
                });
        }

        return Ok(ToResponse(after));
    }

    private static AppSettingsResponse ToResponse(AppSettings s) => new()
    {
        PermissionsGranularGatingEnabled = s.PermissionsGranularGatingEnabled,
        ImportFuzzyMatchThreshold = s.ImportFuzzyMatchThreshold,
        ImportFuzzyMatchGap = s.ImportFuzzyMatchGap,
        RbacMode = s.RbacMode,
    };
}

public sealed class AppSettingsRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("permissions_granular_gating_enabled")]
    public bool PermissionsGranularGatingEnabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("import_fuzzy_match_threshold")]
    public double ImportFuzzyMatchThreshold { get; set; } = 0.8;

    [System.Text.Json.Serialization.JsonPropertyName("import_fuzzy_match_gap")]
    public double ImportFuzzyMatchGap { get; set; } = 0.1;

    [System.Text.Json.Serialization.JsonPropertyName("rbac_mode")]
    public string RbacMode { get; set; } = "legacy";
}

public sealed class AppSettingsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("permissions_granular_gating_enabled")]
    public bool PermissionsGranularGatingEnabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("import_fuzzy_match_threshold")]
    public double ImportFuzzyMatchThreshold { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("import_fuzzy_match_gap")]
    public double ImportFuzzyMatchGap { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("rbac_mode")]
    public string RbacMode { get; set; } = "legacy";
}
