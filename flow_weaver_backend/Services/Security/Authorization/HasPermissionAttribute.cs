using Microsoft.AspNetCore.Authorization;

namespace flow_weaver_backend.Services.Security.Authorization;

// Gates an endpoint on a granular capability from the CapabilityCatalog, e.g.
// [HasPermission("workflow.run")]. Materialised on demand by
// PermissionPolicyProvider into a policy carrying a PermissionRequirement.
//
// Replaces [Authorize(Policy = "Admin"|"Operator"|"Viewer")] on migrated
// controllers. On RbacMode=legacy it behaves identically to the
// capability's legacy tier; on RbacMode=granular it consults the caller's
// permission grants. Admin always passes (bypass).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string capability)
        => Policy = PolicyPrefix + capability;
}
