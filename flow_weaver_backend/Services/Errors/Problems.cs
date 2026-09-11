using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Errors;

// One-call helpers that build a problem+json result with the same
// backwards-compatible shape DomainExceptionHandler emits:
//   { "type": "...", "title": "...", "status": 4xx, "detail": "<message>",
//     "error": "<message>", "code": "<code>" }
//
// The duplicated top-level `error` field keeps the existing frontend
// `body.error` reader working. New clients can read `code` from the
// extensions to react to specific failures without parsing detail.
//
// Use this from controllers when the failure is an inline validation
// you don't want to model as a domain exception (the throwing path
// goes through DomainExceptionHandler — see Services/Errors).
public static class Problems
{
    public static ObjectResult BadRequest(string detail, string? code = null) =>
        Build(400, "Bad Request", detail, code ?? "validation_failed",
            "https://flow-weaver.io/errors/bad-request");

    public static ObjectResult NotFound(string resource, object? key = null) =>
        Build(404, "Not Found",
            key is null ? $"{resource} not found" : $"{resource} '{key}' not found",
            "not_found",
            "https://flow-weaver.io/errors/not-found");

    public static ObjectResult Conflict(string detail, string? code = null) =>
        Build(409, "Conflict", detail, code ?? "conflict",
            "https://flow-weaver.io/errors/conflict");

    public static ObjectResult Forbidden(string detail = "forbidden", string? code = null) =>
        Build(403, "Forbidden", detail, code ?? "forbidden",
            "https://flow-weaver.io/errors/forbidden");

    public static ObjectResult PreconditionFailed(string detail, string? code = null) =>
        Build(412, "Precondition Failed", detail, code ?? "precondition_failed",
            "https://flow-weaver.io/errors/precondition-failed");

    public static ObjectResult PayloadTooLarge(string detail, string? code = null) =>
        Build(413, "Payload Too Large", detail, code ?? "payload_too_large",
            "https://flow-weaver.io/errors/payload-too-large");

    // Internal/unexpected failures the request couldn't recover from.
    // Default code "internal_error" — pass a specific one (e.g.
    // "run_lookup_failed") when the call site has narrowed the cause.
    public static ObjectResult Internal(string detail, string? code = null) =>
        Build(500, "Internal Server Error", detail, code ?? "internal_error",
            "https://flow-weaver.io/errors/internal");

    private static ObjectResult Build(int status, string title, string detail, string code, string type)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = type,
        };
        // Top-level `error` for legacy frontend readers; `code` for new
        // ones. Kept in extensions so they serialize alongside the RFC
        // 7807 fields rather than replacing them.
        problem.Extensions["error"] = detail;
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
