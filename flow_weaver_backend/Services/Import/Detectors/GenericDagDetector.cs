using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Detectors;

// Catch-all for anything that looks DAG-shaped (top-level `nodes` and
// `edges` / `links` arrays) but didn't trip the more specific detectors.
// Low baseline confidence so the agent translator gets invoked — it has
// the language model to guess the semantics. We never beat a specific
// detector here on purpose.
public sealed class GenericDagDetector : IDslDetector
{
    public string FormatName => "generic_dag";

    public double Detect(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) return 0.0;

        // A bundle is DAG-shaped and is not a foreign DSL: it announces itself as
        // ours and carries its own dependencies. Being the catch-all makes this
        // the detector that picks a bundle up once every specific one has declined
        // it — and the agent translator would then guess semantics the file states
        // outright.
        if (Workflow.WorkflowBundleReader.LooksLikeBundle(document)) return 0.0;

        var hasNodes = document.TryGetProperty("nodes", out var nodes)
            && nodes.ValueKind == JsonValueKind.Array
            && nodes.GetArrayLength() > 0;

        var hasEdges =
            (document.TryGetProperty("edges", out var edges)
                && edges.ValueKind == JsonValueKind.Array)
            || (document.TryGetProperty("links", out var links)
                && links.ValueKind == JsonValueKind.Array)
            || (document.TryGetProperty("connections", out var conns)
                && (conns.ValueKind == JsonValueKind.Array || conns.ValueKind == JsonValueKind.Object));

        if (hasNodes && hasEdges) return 0.3;
        if (hasNodes) return 0.15;
        return 0.0;
    }
}
