using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace flow_weaver_backend.Services.Import;

// Computes a structural fingerprint of a workflow's graph so the
// pipeline can spot "this is the same workflow we already have, just
// renamed." Ignores ids, positions, names, descriptions — focuses on
// the shape of the DAG and the type/config of each step.
//
// Two workflows with the same fingerprint are *structurally* identical
// (same handler types in same DAG topology with the same config
// overrides). They may still differ in cosmetics or metadata.
public static class DuplicateFingerprint
{
    // SHA-256 over a normalised representation of (nodes, edges).
    // Stable across import / re-export: the same workflow returns the
    // same fingerprint regardless of node-id naming.
    public static string Compute(JsonElement workflow)
    {
        var sb = new StringBuilder();
        AppendNodes(sb, workflow);
        sb.Append("||");
        AppendEdges(sb, workflow);

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendNodes(StringBuilder sb, JsonElement wf)
    {
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return;

        // Canonical order: by (snippet_id, config_shape) ascending.
        // We DO NOT use node.id because that name is cosmetic — two
        // workflows with `step-1`/`step-2` vs `a`/`b` should match.
        var ordered = nodes.EnumerateArray()
            .Where(n => n.ValueKind == JsonValueKind.Object)
            .Select(n => new
            {
                Sid = GetString(n, "snippet_id"),
                Config = NormaliseConfig(n),
            })
            .OrderBy(x => x.Sid, StringComparer.Ordinal)
            .ThenBy(x => x.Config, StringComparer.Ordinal);

        foreach (var n in ordered)
        {
            sb.Append('N').Append(':').Append(n.Sid).Append(':').Append(n.Config).Append('|');
        }
    }

    private static void AppendEdges(StringBuilder sb, JsonElement wf)
    {
        if (!wf.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            return;

        // Edges by (source_snippet → target_snippet, type). Source/target
        // are looked up against the node map so renaming a node id does
        // not change the fingerprint.
        var nodeIdToSnippet = BuildNodeIdMap(wf);
        var ordered = edges.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => new
            {
                Src = ResolveSnippet(GetString(e, "source"), nodeIdToSnippet),
                Tgt = ResolveSnippet(GetString(e, "target"), nodeIdToSnippet),
                Type = GetString(e, "type"),
            })
            .OrderBy(x => x.Src, StringComparer.Ordinal)
            .ThenBy(x => x.Tgt, StringComparer.Ordinal)
            .ThenBy(x => x.Type, StringComparer.Ordinal);

        foreach (var e in ordered)
        {
            sb.Append('E').Append(':').Append(e.Src).Append(">").Append(e.Tgt).Append(':').Append(e.Type).Append('|');
        }
    }

    private static Dictionary<string, string> BuildNodeIdMap(JsonElement wf)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(n, "id");
            var sid = GetString(n, "snippet_id");
            if (id.Length > 0) map[id] = sid;
        }
        return map;
    }

    private static string ResolveSnippet(string nodeId, Dictionary<string, string> map)
        => map.TryGetValue(nodeId, out var sid) ? sid : nodeId;

    // Normalised config: object key order doesn't change the hash, but
    // any nested data still matters. Empty config maps to the empty
    // string so default-everywhere workflows still hash uniquely on
    // their graph shape.
    private static string NormaliseConfig(JsonElement node)
    {
        if (!node.TryGetProperty("config_overrides", out var co)
            || co.ValueKind != JsonValueKind.Object) return string.Empty;
        return Canonicalise(co);
    }

    private static string Canonicalise(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",",
                el.EnumerateObject()
                  .OrderBy(p => p.Name, StringComparer.Ordinal)
                  .Select(p => $"\"{p.Name}\":{Canonicalise(p.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",",
                el.EnumerateArray().Select(Canonicalise)) + "]",
            JsonValueKind.String => "\"" + (el.GetString() ?? "") + "\"",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "",
        };
    }

    private static string GetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!obj.TryGetProperty(key, out var v)) return string.Empty;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }

    // Match score 0..1 between two fingerprints. Exact match = 1.0;
    // anything else returns a heuristic based on shared prefix length
    // (cheap; could be replaced with a per-node Jaccard later).
    public static double Similarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var prefix = 0;
        var max = Math.Min(a.Length, b.Length);
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        return prefix == max ? 0.95 : 0.0; // Different hashes are different graphs.
    }
}
