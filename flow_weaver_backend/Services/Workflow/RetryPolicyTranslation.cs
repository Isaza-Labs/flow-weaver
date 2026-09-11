using System.Text.Json;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// Reads a bundle snippet's <c>retry_policy</c> into this engine's canonical
/// shape (execution/SPEC.md §3 — FlowWeaver's shape is the oracle's).
/// </summary>
/// <remarks>
/// Nashira's engine stored <c>{ max_attempts, delay_seconds, backoff }</c>.
/// The two shapes count differently: <c>max_attempts</c> includes the first
/// try, <c>max_retries</c> does not — so a policy of "3 attempts" is "2
/// retries", and reading the number across unchanged would grant one retry
/// too many on every imported step. <c>max_delay_seconds</c> is set to 30,
/// the cap Nashira's profile applies, so the imported step never waits longer
/// here than it would have there. Exporters write the canonical shape only.
/// </remarks>
public static class RetryPolicyTranslation
{
    public sealed record Result(JsonElement Policy, bool Translated);

    /// <summary>
    /// Canonical policy for a bundle value: the value itself when it is
    /// already canonical (or absent / not an object — the engine treats a
    /// malformed policy as "no retry", which is the right reading of nothing);
    /// the translated equivalent when it is the legacy shape.
    /// </summary>
    public static Result ToCanonical(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } policy)
            return new Result(default, false);

        var hasCanonical = policy.TryGetProperty("max_retries", out _);
        var hasLegacy = policy.TryGetProperty("max_attempts", out var attemptsEl);
        if (hasCanonical || !hasLegacy)
            return new Result(policy.Clone(), false);

        var attempts = attemptsEl.ValueKind == JsonValueKind.Number && attemptsEl.TryGetInt32(out var a) ? a : 1;
        double delay = 5;
        if (policy.TryGetProperty("delay_seconds", out var d) && d.ValueKind == JsonValueKind.Number)
            delay = d.GetDouble();
        var backoff = policy.TryGetProperty("backoff", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? "exponential"
            : "exponential";

        var canonical = JsonSerializer.SerializeToElement(new Dtos.RetryPolicy
        {
            MaxRetries = Math.Max(0, attempts - 1),
            InitialDelay = delay,
            Backoff = backoff,
            MaxDelay = 30,
        });
        return new Result(canonical, true);
    }
}
