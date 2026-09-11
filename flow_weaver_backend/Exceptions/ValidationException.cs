namespace flow_weaver_backend.Exceptions;

// 400 Bad Request — the request shape or values are invalid (missing
// required field, out-of-range length, malformed identifier).
//
// Optional Details carries a structured list of failures (e.g. a
// JSON-Schema validator's per-field errors). DomainExceptionHandler
// surfaces it as problem+json `extensions.details`, which the frontend
// already reads for the workflow-import commit flow. Leave it null for
// single-message validation failures.
public sealed class ValidationException : DomainException
{
    public IReadOnlyList<string>? Details { get; }

    public ValidationException(string message, string? code = null,
        IReadOnlyList<string>? details = null)
        : base(code ?? "validation_failed", message, 400)
    {
        Details = details;
    }
}
