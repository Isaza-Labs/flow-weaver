namespace flow_weaver_backend.Exceptions;

// 413 Payload Too Large — request body exceeds a fixed cap
// (workflow import upload, etc.).
public sealed class PayloadTooLargeException : DomainException
{
    public PayloadTooLargeException(string message, string? code = null)
        : base(code ?? "payload_too_large", message, 413)
    {
    }
}
