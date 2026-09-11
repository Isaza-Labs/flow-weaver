namespace flow_weaver_backend.Exceptions;

// 403 Forbidden — caller is authenticated but lacks permission for the
// specific resource (per-resource RBAC denial).
// 401 Unauthorized stays a separate axis: handled by [Authorize].
public sealed class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "forbidden", string? code = null)
        : base(code ?? "forbidden", message, 403)
    {
    }
}
