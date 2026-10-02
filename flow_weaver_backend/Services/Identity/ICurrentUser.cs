namespace flow_weaver_backend.Services.Identity;

// Read-only view of the authenticated principal for the current request.
// Injected everywhere a service needs to know who the caller is or decide
// what they are allowed to see. Scoped to the request so the values never
// cross between concurrent requests.
//
// On anonymous requests, accessing UserId throws. Services that legitimately
// need to run anonymously (none today in the CRUD surface) should check
// IsAuthenticated first.
public interface ICurrentUser
{
    Guid UserId { get; }
    string? Username { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAuthenticated { get; }

    // RBAC-granular: an optional per-transport
    // capability ceiling — e.g. a messaging channel's MaxRole expressed as
    // capabilities. null = no ceiling. When set, EffectivePermissions and the
    // [HasPermission] handler intersect the caller's effective capabilities with
    // it: narrowing only, never widening, and it caps admin too, so a channel
    // can never escalate privilege.
    IReadOnlyCollection<string>? CapabilityCeiling { get; }
}
