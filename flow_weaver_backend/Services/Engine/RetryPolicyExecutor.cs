using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Worker;
using Microsoft.Extensions.Logging;

namespace flow_weaver_backend.Services.Engine;

// Applies a snippet's RetryPolicy (execution/SPEC.md §3) to one step.
//
// The worker runs every attempt of a step inside the same job: RunAsync executes the
// handler, and while the failure is `retryable` and the policy has attempts left, waits the
// backoff and runs it again. Retrying in place rather than re-enqueuing keeps one step_run
// row per step — the orchestrator, the UI and the run outcome all see a single step whose
// logs name every attempt — and needs no delayed-job support from the queue.
//
// Singleton safe — no state.
public sealed class RetryPolicyExecutor
{
    private readonly ILogger<RetryPolicyExecutor> _logger;

    public RetryPolicyExecutor(ILogger<RetryPolicyExecutor> logger)
    {
        _logger = logger;
    }

    // Runs `attempt` until it succeeds, fails for good, or the policy is spent.
    //
    // A failure is retried only when ALL of these hold:
    //   - the handler marked it Retryable (the SPEC §3 gate);
    //   - it reported no change — a retry repeats the action, and an attempt that already
    //     changed something must not be repeated whatever the handler thinks;
    //   - the policy allows another attempt.
    //
    // `beforeRetry` is called with the backoff before every retry and does the waiting; the
    // worker also renews its job lease there and checks the step is still wanted. It returns
    // false to abandon the step (claim lost, step timed out, run cancelled), in which case the
    // outcome is flagged Abandoned and the caller must not record the result.
    public async Task<RetryOutcome> RunAsync(
        RetryPolicy policy,
        Func<CancellationToken, Task<SnippetResult>> attempt,
        Func<TimeSpan, CancellationToken, Task<bool>> beforeRetry,
        CancellationToken ct)
    {
        var log = new System.Text.StringBuilder();
        var attempts = 0;
        while (true)
        {
            attempts++;
            var result = await attempt(ct);
            if (result.Success)
            {
                if (attempts > 1) LogSuccess(attempts);
                return new RetryOutcome(result, attempts, log.ToString(), Abandoned: false);
            }

            var retry = attempts; // 1-based index of the retry this failure would trigger
            // MaxRetries first: a snippet with no policy (the common case) must not log an
            // "exhausted" warning on every retryable failure.
            if (policy.MaxRetries <= 0 || !result.Retryable || result.Change == StepChange.Changed
                || !ShouldRetry(policy, retry))
                return new RetryOutcome(result, attempts, log.ToString(), Abandoned: false);

            var delay = GetDelay(policy, retry);
            log.AppendLine(
                $"attempt {attempts} failed: {result.Error} — retrying in {delay.TotalSeconds:0.#}s "
                + $"(retry {retry} of {policy.MaxRetries})");
            if (!string.IsNullOrEmpty(result.Logs)) log.AppendLine(result.Logs);

            if (!await beforeRetry(delay, ct))
            {
                _logger.LogWarning(
                    "engine.retry.abandoned attempts={Attempts} max={Max}", attempts, policy.MaxRetries);
                return new RetryOutcome(result, attempts, log.ToString(), Abandoned: true);
            }
        }
    }

    // Calculate the delay before the Nth retry. `attempt` is 1-based:
    // 1 = first retry, 2 = second retry, etc.
    public TimeSpan GetDelay(RetryPolicy policy, int attempt)
    {
        // Clamped at zero: a negative delay in a hand-edited policy is "no wait", not a crash
        // in Task.Delay.
        var delay = TimeSpan.FromSeconds(Math.Max(0, Math.Min(BackoffSeconds(policy, attempt), policy.MaxDelay)));
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

// What RunAsync hands back: the last attempt's result, how many attempts ran, and the log of
// the attempts that failed before it (empty when the first attempt was final).
public sealed record RetryOutcome(SnippetResult Result, int Attempts, string RetryLog, bool Abandoned);
