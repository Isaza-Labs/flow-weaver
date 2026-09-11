using Microsoft.AspNetCore.Authorization;

namespace flow_weaver_backend.Services.Security.Authorization;

// Authorization requirement carrying the capability key a [HasPermission]
// endpoint demands. Resolved by PermissionAuthorizationHandler.
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Capability { get; }

    public PermissionRequirement(string capability) => Capability = capability;
}
