using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Permission;

// Resource-scoped roles. Order matters: indexed comparisons in
// HasAtLeastAsync compare by position in this array.
public static class ResourceRoles
{
    public const string Owner = "owner";
    public const string Editor = "editor";
    public const string Runner = "runner";
    public const string Viewer = "viewer";

    // Higher-index = stronger. Used by ResourcePermissionService to
    // determine the effective role when a subject has multiple grants
    // and to evaluate "X requires at least editor".
    public static readonly IReadOnlyList<string> Order = new[]
    {
        Viewer, Runner, Editor, Owner,
    };

    public static int Rank(string role)
    {
        for (var i = 0; i < Order.Count; i++)
            if (string.Equals(Order[i], role, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

public static class ResourceTypes
{
    public const string Workflow = "workflow";
    public const string Integration = "integration";

    public static bool IsKnown(string? type) =>
        string.Equals(type, Workflow, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, Integration, StringComparison.OrdinalIgnoreCase);
}

public interface IResourcePermissionService
{
    Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(
        string resourceType, Guid resourceId, CancellationToken ct);

    Task<ResourcePermissionResponse> GrantAsync(
        string resourceType, Guid resourceId, GrantResourcePermissionRequest dto, CancellationToken ct);

    Task RevokeAsync(Guid permissionId, CancellationToken ct);

    // Returns true when the current user satisfies `requiredRole` for
    // (resourceType, resourceId). Global Admin always passes; Operator
    // satisfies up to runner; Viewer satisfies viewer. Per-resource
    // grants RAISE the floor — never lower it.
    Task<bool> HasAtLeastAsync(
        string resourceType, Guid resourceId, string requiredRole, CancellationToken ct);
}
