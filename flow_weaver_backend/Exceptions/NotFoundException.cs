namespace flow_weaver_backend.Exceptions;

// 404 Not Found — the addressed resource does not exist for the caller
// (true 404 or a row the caller is not allowed to see). DomainExceptionHandler
// translates this to a problem+json 404 with `extensions.code = "not_found"`.
public class NotFoundException : DomainException
{
    public NotFoundException()
        : base("not_found", "Resource not found", 404)
    {
    }

    public NotFoundException(string message)
        : base("not_found", message, 404)
    {
    }

    public NotFoundException(string resourceName, object key)
        : base("not_found", $"{resourceName} with key '{key}' was not found", 404)
    {
    }
}
