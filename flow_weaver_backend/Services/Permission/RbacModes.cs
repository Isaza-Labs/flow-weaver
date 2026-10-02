namespace flow_weaver_backend.Services.Permission;

// Rollout switch for the granular RBAC HTTP layer.
// In `legacy` mode a [HasPermission("cap")] gate
// reproduces the capability's legacy tier (Admin/Operator/Viewer), so migrating
// controllers to the attribute changes nothing. Flip to `granular` to
// enforce the actual permission grants via IEffectivePermissions.
public static class RbacModes
{
    public const string Legacy = "legacy";
    public const string Granular = "granular";

    public static string Normalize(string? mode) =>
        string.Equals(mode, Granular, StringComparison.OrdinalIgnoreCase) ? Granular : Legacy;

    public static bool IsGranular(string? mode) =>
        string.Equals(mode, Granular, StringComparison.OrdinalIgnoreCase);
}
