using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

// An Integration bundles a base URL + auth config with its own catalog of
// IntegrationAction rows (discovered from an uploaded OpenAPI spec) and
// the prompt skills / specs the agent uses to talk to it.

public class Integration : BaseModel
{
    public Guid IntegrationId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Stable cross-instance identity, derived from Name at creation and never
    // changed afterwards. A workflow bundle exported elsewhere names this
    // integration by slug, so renaming the integration must not break bundles
    // already shared. Nullable only so the backfill can run on existing rows;
    // every row created after that migration has one. See Services.Common.Slug.
    public string? Slug { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseURL { get; set; } = string.Empty;

    // AuthConfig holds secrets (tokens, basic auth). It is encrypted at rest by
    // AppDbContext (IntegrationAuthCipher); services always see plaintext.
    [JsonIgnore]
    public JsonElement AuthConfig { get; set; } = default;

    public JsonElement Headers { get; set; } = default;
    public bool TLSSkipVerify { get; set; }

    // SSRF guard opt-out for THIS integration only. The default (false)
    // makes the runtime block calls to private/loopback/link-local
    // ranges per IUrlGuard. Self-hosted deployments where NetBox /
    // similar systems run on RFC-1918 networks (10/8, 172.16/12,
    // 192.168/16) flip this to true on a per-integration basis. The
    // flag is admin-controlled in the UI; viewer/operator can't change
    // it. It does NOT bypass loopback or 169.254.169.254 (metadata IP)
    // — those stay blocked regardless.
    public bool AllowPrivateNetwork { get; set; }

    public JsonElement HealthCheck { get; set; } = default;
    public string Status { get; set; } = string.Empty;
    public DateTime? LastCheckedAt { get; set; }
    public bool Enabled { get; set; }
}
