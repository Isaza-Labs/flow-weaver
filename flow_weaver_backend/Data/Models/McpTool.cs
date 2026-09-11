using System.Text.Json;

namespace flow_weaver_backend.Models;

// A tool discovered on an McpServer (upserted from the server's tools/list).
// Cached so a workflow node / the agent can reference it without a live round
// trip, and so the builder can browse the catalog. Mirrors IntegrationAction.
// Unique per (McpServerId, Name) among active rows.
public class McpTool : BaseModel
{
    public Guid McpToolId { get; set; }
    public Guid McpServerId { get; set; }

    // The tool's name on the server (the id passed to tools/call).
    public string Name { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }

    // JSON Schema of the tool's arguments, as reported by the server (jsonb).
    public JsonElement InputSchema { get; set; } = default;

    public bool Enabled { get; set; } = true;
}
