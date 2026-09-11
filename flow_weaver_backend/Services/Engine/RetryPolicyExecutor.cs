using System.Text.Json;
using flow_weaver_backend.Dtos;
using Microsoft.Extensions.Logging;

namespace flow_weaver_backend.Services.Engine;

// Stateless helper that reads the RetryPolicy jsonb from a
// Snippet and tells callers how long to wait between retries.
// Used by the orchestrator when a step fails and the policy allows
// re-enqueuing.
//
// Singleton safe — no state.
public sealed class RetryPolicyExecutor
{
    private readonly ILogger<RetryPolicyExecutor> _logger;

    public RetryPolicyExecutor(ILogger<RetryPolicyExecutor> logger)
    {
        _logger = logger;
    }

    // Calculate the delay before the Nth retry. `attempt` is 1-based:
    // 1 = first retry, 2 = second retry, etc.
    public TimeSpan GetDelay(RetryPolicy policy, int attempt)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(BackoffSeconds(policy, attempt), policy.MaxDelay));
        _logger.LogInformation(
            "engine.retry.scheduled attempt={Attempt} max={Max} backoff={Backoff} delay_ms={DelayMs}",
            attempt, policy.MaxRetries, policy.Backoff, (long)delay.TotalMilliseconds);
        return delay;
    }

    public bool ShouldRetry(RetryPolicy policy, int attempt)
    {
        var allowed = policy.MaxRetries > 0 && attempt <= policy.MaxRetries;
        if (allowed)
        {
            _logger.LogDebug(
                "engine.retry.attempt attempt={Attempt} max={Max}",
                attempt, policy.MaxRetries);
        }
        else
        {
            _logger.LogWarning(
                "engine.retry.exhausted attempt={Attempt} max={Max}",
                attempt, policy.MaxRetries);
        }
        return allowed;
    }

    // Record a successful terminal attempt. Called by the executor when a
    // step finally succeeds so we can observe total retry counts.
    public void LogSuccess(int attempts)
    {
        _logger.LogInformation("engine.retry.success attempts={Attempts}", attempts);
    }

    // Parse the jsonb column into a typed DTO. Falls back to a zero-retry
    // policy if the column is null/empty/malformed so callers never need
    // to null-check the result.
    public static RetryPolicy Parse(JsonElement retryPolicyJson)
    {
        if (retryPolicyJson.ValueKind != JsonValueKind.Object)
            return new RetryPolicy();

        try
        {
            return JsonSerializer.Deserialize<RetryPolicy>(retryPolicyJson.GetRawText())
                   ?? new RetryPolicy();
        }
        catch
        {
            return new RetryPolicy();
        }
    }

    private static double BackoffSeconds(RetryPolicy p, int attempt) => p.Backoff switch
    {
        "exponential" => p.InitialDelay * Math.Pow(2, attempt - 1),
        "linear" => p.InitialDelay * attempt,
        "fixed" => p.InitialDelay,
        _ => p.InitialDelay,
    };
}
