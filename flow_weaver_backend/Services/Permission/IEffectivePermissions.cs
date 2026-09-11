namespace flow_weaver_backend.Services.Permission;

// Resolves a caller's effective granular permissions from their
// PermissionGrants. Scoped: grants are loaded once per request/turn and cached
// on the instance. admin bypasses every check. See plan_rbac_granular.md §5.
//
// Two layers:
//   • HasAsync(cap, ctx)   — contextual (ABAC) decision at an enforcement point.
//   • CapabilitiesAsync()  — coarse set for the [HasPermission] fast-reject and
//                            for building the agent's available-tool list.
public interface IEffectivePermissions
{
    // Does the caller hold `capability` under this call context?
    Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default);

    // Unconditional check — equivalent to HasAsync(capability, PermissionContext.Global).
    Task<bool> HasAsync(string capability, CancellationToken ct = default);

    // Every capability the caller holds in ANY context (conditions ignored).
    Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default);
}
