namespace flow_weaver_backend.Exceptions;

// 401 Unauthorized — the caller is not authenticated (missing/invalid
// token). Use ForbiddenException for "authenticated but not allowed".
public class UnauthorizedException : DomainException
{
    public UnauthorizedException()
        : base("unauthorized", "Unauthorized access", 401)
    {
    }

    public UnauthorizedException(string message)
        : base("unauthorized", message, 401)
    {
    }
}
