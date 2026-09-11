namespace flow_weaver_backend.Exceptions;

// Base for every exception the HTTP surface should translate to a
// well-formed ProblemDetails response. Services throw these instead of
// returning ad-hoc `{ error = "..." }` payloads; DomainExceptionHandler
// (Services/Errors) maps them to the right status code and writes a
// ProblemDetails body that also carries a top-level `error` field for
// backwards compatibility with the frontend.
//
// Status is set per-subtype (Validation→400, NotFound→404, Conflict→409, …)
// and the optional Code becomes problem+json `extensions.code`, so a
// client can react to (e.g.) "simulation_stale" without parsing Detail.
public abstract class DomainException : Exception
{
    public string Code { get; }
    public int Status { get; }

    protected DomainException(string code, string message, int status)
        : base(message)
    {
        Code = code;
        Status = status;
    }
}
