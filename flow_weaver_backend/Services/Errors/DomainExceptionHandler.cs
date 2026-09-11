using flow_weaver_backend.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace flow_weaver_backend.Services.Errors;

// Translates DomainException subclasses into problem+json responses
// with the same body shape as Services/Errors/Problems.cs.
//
// Registered via builder.Services.AddExceptionHandler<DomainExceptionHandler>()
// + app.UseExceptionHandler() in Program.cs. Any other (unknown)
// exception falls through to the framework default, which becomes a
// generic 500 — that is intentional: we don't want to leak internal
// failure messages.
public sealed class DomainExceptionHandler(
    ILogger<DomainExceptionHandler> logger,
    ProblemDetailsFactory factory) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainException domain)
            return false;

        // 4xx errors are operator/caller mistakes, not server bugs — log
        // at Warning so they don't trip 5xx alerting, but still appear
        // in audit logs with the code + path for triage.
        logger.LogWarning(exception,
            "domain.error code={Code} status={Status} path={Path}",
            domain.Code, domain.Status, ctx.Request.Path);

        ctx.Response.StatusCode = domain.Status;
        ctx.Response.ContentType = "application/problem+json";

        var problem = factory.CreateProblemDetails(
            ctx,
            statusCode: domain.Status,
            title: TitleFor(domain.Status),
            type: TypeFor(domain.Status),
            detail: domain.Message);

        // Legacy frontend reads `body.error`; new clients can pivot on
        // `code` (extensions). Both live in the same JSON object.
        problem.Extensions["error"] = domain.Message;
        problem.Extensions["code"] = domain.Code;

        // ValidationException can carry a per-field error list (schema
        // validator output, etc.). Emit it as `extensions.details` so
        // the import wizard's commit-failed toast (which already reads
        // body.details) keeps working when callers migrate from the
        // legacy `BadRequest(new { error, details })` shape.
        if (domain is ValidationException v && v.Details is { Count: > 0 })
            problem.Extensions["details"] = v.Details;

        // The content type has to be passed here, not just assigned above:
        // WriteAsJsonAsync overwrites Response.ContentType with
        // "application/json" otherwise, and clients that branch on
        // problem+json would never take that path.
        await ctx.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", ct);
        return true;
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        412 => "Precondition Failed",
        413 => "Payload Too Large",
        _ => "Error",
    };

    private static string TypeFor(int status) => status switch
    {
        400 => "https://flow-weaver.io/errors/bad-request",
        401 => "https://flow-weaver.io/errors/unauthorized",
        403 => "https://flow-weaver.io/errors/forbidden",
        404 => "https://flow-weaver.io/errors/not-found",
        409 => "https://flow-weaver.io/errors/conflict",
        412 => "https://flow-weaver.io/errors/precondition-failed",
        413 => "https://flow-weaver.io/errors/payload-too-large",
        _ => "about:blank",
    };
}
