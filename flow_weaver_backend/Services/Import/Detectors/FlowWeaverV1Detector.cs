using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Detectors;

// Recognises our own v1 export. The hard markers:
//   • top-level "schema_version" == "v1", OR
//   • top-level "nodes" array whose first element has the v1-specific
//     keys (snippet_id, config_overrides).
//
// Falls back to a lower confidence when only the loose shape matches —
// the user can still force detection via the format_hint parameter.
public sealed class FlowWeaverV1Detector : IDslDetector
{
    public string FormatName => "flow_weaver_v1";

    public double Detect(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) return 0.0;

        // A bundle is not a foreign format that resembles ours — it IS ours, and
        // it carries its own dependencies. On shape alone it scores 0.85 here
        // (top-level nodes/edges, snippet_id on the first node), and translating
        // it discards `dependencies`: the snippet types then have to be inferred,
        // and a `ping` and an `email_send` both come back as `python_snippet`.
        //
        // The pipeline short-circuits bundles before detection runs. This is the
        // second lock on the same door, for any route that reaches a detector
        // without passing that check.
        if (Workflow.WorkflowBundleReader.LooksLikeBundle(document)) return 0.0;

        var hasSchemaVersion = document.TryGetProperty("schema_version", out var sv)
            && sv.ValueKind == JsonValueKind.String
            && string.Equals(sv.GetString(), "v1", StringComparison.OrdinalIgnoreCase);

        var hasNodes = document.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array;
        var hasEdges = document.TryGetProperty("edges", out var edges)
            && edges.ValueKind == JsonValueKind.Array;

        if (!hasNodes || !hasEdges) return 0.0;

        // Look at the first node to confirm v1-specific keys.
        var firstNode = nodes.EnumerateArray().FirstOrDefault();
        var v1NodeKeys = firstNode.ValueKind == JsonValueKind.Object
            && firstNode.TryGetProperty("snippet_id", out _);

        if (hasSchemaVersion && v1NodeKeys) return 1.0;
        if (v1NodeKeys) return 0.85;
        return 0.0;
    }
}
