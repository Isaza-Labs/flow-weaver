using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Response of POST /integration/{id}/health. Shape is stable across both the
// success path (status + status_code + expected) and the failure path
// (status + error) by using nullable fields with WhenWritingNull ignore.
public class IntegrationHealthCheckResult
{
    // "healthy", "degraded" (reachable but the configured credentials were
    // never validated by any probed endpoint) or "unhealthy"
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("status_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; set; }

    [JsonPropertyName("expected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Expected { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    // Only set when the integration has credentials configured and the check
    // ran in lenient (no explicit health config) mode:
    //   true  — some probed endpoint rejected the anonymous twin of a request
    //           that succeeded WITH credentials, i.e. the token was actually
    //           validated upstream.
    //   false — every probed endpoint accepts anonymous requests, so this
    //           check proves reachability only; an invalid token would go
    //           unnoticed (status is "degraded" in that case).
    [JsonPropertyName("auth_verified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AuthVerified { get; set; }
}
