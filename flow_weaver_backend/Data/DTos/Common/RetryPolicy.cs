using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

public class RetryPolicy
{
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; }

    [JsonPropertyName("initial_delay_seconds")]
    public double InitialDelay { get; set; } = 5;

    // "exponential", "linear", "fixed"
    [JsonPropertyName("backoff")]
    public string Backoff { get; set; } = "exponential";

    [JsonPropertyName("max_delay_seconds")]
    public double MaxDelay { get; set; } = 300;
}
