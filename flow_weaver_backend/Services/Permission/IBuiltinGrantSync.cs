namespace flow_weaver_backend.Services.Permission;

// Keeps the two built-in permission grants (builtin.operator / builtin.viewer)
// present and their membership in sync with each user's legacy
// User.Role. This is the phase-1 "dual-write" of plan_rbac_granular.md:
// User.Role stays the source of truth and built-in grant membership mirrors
// it, so the granular model reproduces today's behaviour while the rest of the
// refactor lands. Admins layer their own grants on top independently.
public interface IBuiltinGrantSync
{
    // Get-or-create the two built-in grants and refresh their capability
    // sets from the catalogue. Idempotent.
    Task EnsureGrantsAsync(CancellationToken ct = default);

    // Make `userId`'s built-in membership match `role`: a subject of the
    // matching bundle, absent from the others. admin → subject of none, because
    // admin bypasses every permission check.
    Task SyncUserAsync(Guid userId, string role, CancellationToken ct = default);
}
