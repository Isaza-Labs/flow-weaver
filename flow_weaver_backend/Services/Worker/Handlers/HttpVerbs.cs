namespace flow_weaver_backend.Services.Worker.Handlers;

// The one place both HTTP-shaped handlers ask "did this request mean to write?".
//
// Copied verbatim from Nashira's CoreHandlers.cs — same name, same rule, same
// namespace shape — because it is the change signal's definition for HTTP, not
// either product's opinion about it. A handler that can read the verb can MEASURE
// its own effect instead of deferring to the author.
//
// GET/HEAD/OPTIONS are the safe methods RFC 9110 §9.2.1 defines as read-only.
// Everything else is treated as a write, including the ones a particular API may
// implement idempotently: a tier says an action COULD be undone, this says
// something WAS done.
internal static class HttpVerbs
{
    public static bool Mutating(string? method) =>
        !string.IsNullOrWhiteSpace(method)
        && !method.Trim().Equals("GET", StringComparison.OrdinalIgnoreCase)
        && !method.Trim().Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        && !method.Trim().Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    // Whether a failed HTTP call may be retried (execution/SPEC.md §3 `retryable`).
    //
    // The question is not "is this error transient" but "is it safe to send this again", and
    // for a write the two differ: a 502 or a timeout on a POST says nothing about whether the
    // upstream applied it. So a read is retried on any transient answer, and a write only when
    // the request provably never took effect — it never reached the server, or the server
    // refused it before processing (429, 503).
    public static bool RetryableStatus(string? method, int statusCode) =>
        Mutating(method)
            ? statusCode is 429 or 503
            : statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    public static bool RetryableTransportFailure(string? method, HttpRequestException ex) =>
        !Mutating(method)
        || ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

    // A timeout on a write may have landed after the client gave up waiting.
    public static bool RetryableTimeout(string? method) => !Mutating(method);
}
