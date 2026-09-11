using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Authorization;

namespace flow_weaver_backend.Services.Security.Authorization;

// Resolves [HasPermission("cap")] gates. Scoped, so it can use the request's
// ICurrentUser / IEffectivePermissions.
//
//   1. admin bypasses everything (both modes).
//   2. RbacMode=granular  → succeed if the caller's grants include the
//                           capability in ANY context (coarse gate; the
//                           env/device-conditioned check happens deeper, in
//                           the service layer).
//   3. RbacMode=legacy    → reproduce the capability's legacy tier via a role
//                           check, so a migrated controller behaves exactly
//                           like its old [Authorize(Policy=…)].
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly ICurrentUser _caller;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _settings;

    public PermissionAuthorizationHandler(
        ICurrentUser caller, IEffectivePermissions effective, IAppSettingsService settings)
    {
        _caller = caller;
        _effective = effective;
        _settings = settings;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!_caller.IsAuthenticated) return;

        // Transport ceiling (e.g. a messaging channel) caps everyone, admin
        // included — checked before the admin bypass so a channel can never
        // escalate privilege.
        var ceiling = _caller.CapabilityCeiling;
        if (ceiling is not null
            && !ceiling.Contains(requirement.Capability, StringComparer.OrdinalIgnoreCase))
            return;

        // admin bypasses every permission check (within the ceiling).
        if (IsAdmin()) { context.Succeed(requirement); return; }

        var settings = await _settings.GetAsync();
        if (RbacModes.IsGranular(settings.RbacMode))
        {
            var caps = await _effective.CapabilitiesAsync();
            if (caps.Contains(requirement.Capability)) context.Succeed(requirement);
            return;
        }

        // Legacy mode: reproduce the capability's original tier. An unknown
        // capability degrades to admin-only (safe), and admin already passed.
        var tier = CapabilityCatalog.Find(requirement.Capability)?.LegacyTier ?? LegacyTier.Admin;
        var ok = tier switch
        {
            LegacyTier.Viewer => true,                    // any authenticated user
            LegacyTier.Operator => HasRole("operator"),   // admin already handled above
            _ => false,
        };
        if (ok) context.Succeed(requirement);
    }

    private bool IsAdmin() => HasRole("admin");

    private bool HasRole(string role) =>
        _caller.Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}
