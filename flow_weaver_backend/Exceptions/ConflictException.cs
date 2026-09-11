namespace flow_weaver_backend.Exceptions;

// 409 Conflict — the request is well-formed but the system state
// rejects it (duplicate key, workflow not runnable, simulation stale).
public sealed class ConflictException : DomainException
{
    public ConflictException(string message, string? code = null)
        : base(code ?? "conflict", message, 409)
    {
    }
}
