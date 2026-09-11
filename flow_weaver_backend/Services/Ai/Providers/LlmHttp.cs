namespace flow_weaver_backend.Services.Ai.Providers;

// URL joining and POST-with-retry, shared by the HTTP providers.
//
// The retry half was already here twice (OpenAiProvider, GeminiProvider) and
// missing from the other two, so an Anthropic or Ollama call died on the first 429
// while an OpenAI call rode it out. It lives once now, keyed by the `tag` each
// caller passes so the logs still name the provider. The request is built per
// attempt because an HttpRequestMessage cannot be sent twice.
internal static class LlmHttp
{
    // Joins a configured base URL with a provider's endpoint path without repeating
    // a segment the base already ends with.
    //
    // This exists because of what the vendors publish. DeepSeek documents its base
    // URL as https://api.deepseek.com/v1, Gemini's OpenAI-compatible one as
    // https://generativelanguage.googleapis.com/v1beta/openai, Moonshot's with /v1
    // — the OpenAI SDK expects the version in the base — so pasting the documented
    // value into the Base URL field is the normal case, not a mistake. Concatenating
    // then produces /v1/v1/chat/completions: a 404 that reads like a bad key or a
    // dead endpoint, and the field looks correct because it is what the vendor's own
    // page says.
    //
    // The longest overlap wins, so /v1beta/openai is matched before /v1.
    public static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = "/" + path.Trim('/');
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var take = segments.Length - 1; take >= 1; take--)
        {
            var prefix = "/" + string.Join('/', segments.Take(take));
            if (b.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return b + p[prefix.Length..];
        }
        return b + p;
    }

    // Retry only these upstream transient codes. Anything else (400 bad request,
    // 401 auth, 403 permission, 404 model-not-found) is fatal on the first try —
    // retrying would just burn latency and duplicate the same error. 408 is included
    // because gateways occasionally surface timeouts as 408 instead of 504.
    private static readonly HashSet<int> RetryableStatusCodes = [408, 429, 500, 502, 503, 504];

    // Backoff 0.5s, 1s, 2s — worst case ~3.5s before giving up. Jitter (±40%)
    // spreads concurrent retries from parallel conversations so a brief outage is
    // not followed by a lockstep stampede. Matches RestOperationExecutor's cadence.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, Func<HttpRequestMessage> buildRequest, string tag, ILogger? logger, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; ; attempt++)
        {
            response?.Dispose();

            using var req = buildRequest();

            try
            {
                response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                // Network-level failure (DNS, socket reset, TLS): no status code was
                // observed, so it is treated as transient, like a 503.
                logger?.LogWarning("{Tag}.post.network_retry attempt={Attempt} error={Error}", tag, attempt + 1, ex.Message);
                await DelayWithJitterAsync(RetryDelays[attempt], ct);
                continue;
            }

            if ((int)response.StatusCode < 400)
                return response;

            var status = (int)response.StatusCode;
            if (!RetryableStatusCodes.Contains(status) || attempt >= RetryDelays.Length)
            {
                // Read the upstream error body before throwing. The real reason
                // (error.message / error.code / error.param) is in there, and
                // EnsureSuccessStatusCode discards it — which is why a 400 used to
                // land in the log with no detail at all.
                logger?.LogError("{Tag}.post.error status={Status} body={Body}",
                    tag, status, await SafeReadBodyAsync(response, ct));
                response.EnsureSuccessStatusCode();
                return response;
            }

            var delay = RetryDelays[attempt];
            if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero)
                delay = ra > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : ra;

            logger?.LogWarning("{Tag}.post.retry attempt={Attempt} status={Status} delay_ms={Delay}",
                tag, attempt + 1, status, (int)delay.TotalMilliseconds);
            await DelayWithJitterAsync(delay, ct);
        }
    }

    // Best-effort read of the upstream error body for logging. Never throws — a
    // failure to read the diagnostic payload must not mask the HTTP error about to
    // be surfaced through EnsureSuccessStatusCode.
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? "(empty)" : body;
        }
        catch (Exception ex)
        {
            return $"(failed to read error body: {ex.Message})";
        }
    }

    private static Task DelayWithJitterAsync(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = (int)(baseDelay.TotalMilliseconds * 0.4);
        var offset = Random.Shared.Next(-jitterMs, jitterMs + 1);
        var final = baseDelay + TimeSpan.FromMilliseconds(offset);
        if (final < TimeSpan.Zero) final = TimeSpan.Zero;
        return Task.Delay(final, ct);
    }
}
