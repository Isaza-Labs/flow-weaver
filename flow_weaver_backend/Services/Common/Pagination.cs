namespace flow_weaver_backend.Services.Common;

// Small helper shared across every <Entity>Service.GetAsync to keep the
// same bounds for limit/offset without duplicating the same 3 lines.
public static class Pagination
{
    // Largest page size we accept. Anything bigger is almost certainly a
    // bug or an attempt to blow through the rate limiter in one request.
    public const int MaxLimit = 200;

    public static (int limit, int offset) Clamp(int limit, int offset) =>
        (Math.Clamp(limit, 1, MaxLimit), Math.Max(0, offset));
}
