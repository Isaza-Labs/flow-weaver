using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Detectors;

// n8n workflow JSON exports have a recognisable signature:
//   { "name": "...", "nodes": [...], "connections": { ... }, ... }
// where each node has a `type` like "n8n-nodes-base.httpRequest" and
// `parameters`. The presence of a "connections" object (not array) is
// the strongest single marker.
public sealed class N8nDetector : IDslDetector
{
    public string FormatName => "n8n";

    public double Detect(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) return 0.0;

        var hasNodes = document.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array
            && nodes.GetArrayLength() > 0;
        var hasConnections = document.TryGetProperty("connections", out var conns)
            && conns.ValueKind == JsonValueKind.Object;
        if (!hasNodes || !hasConnections) return 0.0;

        // Confirm a node carries n8n's namespaced type string.
        var firstNode = nodes.EnumerateArray().FirstOrDefault();
        var hasN8nNodeType = firstNode.ValueKind == JsonValueKind.Object
            && firstNode.TryGetProperty("type", out var nodeType)
            && nodeType.ValueKind == JsonValueKind.String
            && (nodeType.GetString() ?? "").StartsWith("n8n-nodes-", StringComparison.Ordinal);

        if (hasN8nNodeType) return 0.95;
        // Less specific match: only "nodes" + "connections" — could still be n8n.
        return 0.6;
    }
}
