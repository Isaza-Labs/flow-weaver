namespace flow_weaver_backend.Services.Permission;

// The signals a granular capability can be conditioned on, assembled by the
// enforcement point (controller / service / handler) from whatever it has on
// hand — mirrors the PolicyEvaluator context vocabulary.
//
// Null/empty fields mean "not supplied". A grant that conditions on a
// dimension the context did not supply does NOT match: these are ALLOW grants,
// so a scoped grant must never leak into an unscoped call (see
// PermissionConditionMatcher).
public sealed record PermissionContext(
    string? Environment = null,
    IReadOnlyList<string>? DeviceRoles = null,
    IReadOnlyList<string>? DevicePoolNames = null,
    IReadOnlyList<Guid>? DeviceIds = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    Guid? McpServerId = null,
    string? McpToolName = null)
{
    // Unconditional check — matches only grants that declare no conditions.
    public static readonly PermissionContext Global = new();

    // Convenience for the common resource-scoped check.
    public static PermissionContext ForResource(string resourceType, Guid resourceId)
        => new(ResourceType: resourceType, ResourceId: resourceId);
}
