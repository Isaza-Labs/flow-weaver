using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

// A registered external MCP (Model Context Protocol) server. Flow-weaver acts as
// an MCP *client*: it connects over Streamable HTTP, discovers the server's tools
// (cached as McpTool rows), and lets the agent / workflow nodes call them.
// Mirrors the Integration shape.
public class McpServer : BaseModel
{
    public Guid McpServerId { get; set; }
    public string Name { get; set; } = string.Empty;

    // The server's HTTP endpoint (Streamable HTTP / SSE). Passed through
    // IUrlGuard.EnsureSafe before every connection unless AllowPrivateNetwork.
    public string Url { get; set; } = string.Empty;

    // Transport kind. v1 only supports "http" (remote Streamable HTTP); stdio
    // is intentionally out of scope — it would mean spawning and supervising
    // local child processes on the backend host.
    public string Transport { get; set; } = "http";

    // How flow-weaver authenticates TO the server:
    //   none | api_key | bearer | oauth_client_credentials | oauth_authorization_code
    public string AuthType { get; set; } = "none";

    // Auth material (api key / bearer token / OAuth client id+secret+endpoints+
    // scopes, and obtained OAuth access/refresh tokens). Serialized as an
    // McpAuthConfig JSON document then encrypted at the service layer with
    // ICredentialEncryptionService. Never returned to clients (has_* booleans
    // are surfaced instead). Stored as bytea, so it is NOT the jsonb converter's
    // concern.
    [JsonIgnore]
    public byte[]? AuthConfigEncrypted { get; set; }

    // Extra static, NON-secret headers sent on every request (jsonb).
    public JsonElement Headers { get; set; } = default;

    public bool TLSSkipVerify { get; set; }

    // SSRF guard opt-out for THIS server only (admin-controlled). Same semantics
    // as Integration.AllowPrivateNetwork: relaxes RFC-1918 ranges but never
    // loopback / link-local / 169.254.169.254.
    public bool AllowPrivateNetwork { get; set; }

    public bool Enabled { get; set; } = true;

    // ok | needs_config | needs_authorization | unreachable
    public string Status { get; set; } = "needs_config";

    // RESERVED — intended to hold the MCP protocol version negotiated on the
    // last successful connect, but IMcpClient never surfaces it, so no code
    // path has ever written it. Not exposed by the API. Plumb it through the
    // connection factory (or drop the column) before use.
    public string? ProtocolVersion { get; set; }

    public DateTime? LastToolsSyncedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
}
