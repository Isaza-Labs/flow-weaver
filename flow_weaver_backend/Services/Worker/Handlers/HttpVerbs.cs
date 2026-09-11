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
}
