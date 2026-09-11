namespace flow_weaver_backend.Exceptions;

// 412 Precondition Failed — a prerequisite the caller controls is not
// satisfied. Used by S16 harness gates (simulation_missing, _failed,
// _stale) on draft→qa promotion.
public sealed class PreconditionFailedException : DomainException
{
    public PreconditionFailedException(string message, string? code = null)
        : base(code ?? "precondition_failed", message, 412)
    {
    }
}
