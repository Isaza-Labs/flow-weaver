using System.Text.Json;

namespace flow_weaver_backend.Services.Engine;

// Server-side DAG layout applied when the agent (or any API caller) saves
// a workflow without picking coordinates and edge handles. The frontend
// has the same algorithm — keeping this parallel implementation means
// workflows land pre-arranged in storage, so reopening the editor on a
// fresh browser, exporting YAML, or reading the row from a script all
// see the same clean left-to-right layout the user expects.
//
// Algorithm: simplified Sugiyama.
//   1. Rank = longest distance from any source (nodes with no incoming
//      edges). Puts `__start__` in column 0 and pushes downstream nodes
//      one column further on the longest path, which is what makes
//      branches visually line up with their joins.
//   2. Within each rank, order by the average rank-position of
//      predecessors (barycenter). A couple of passes converges for the
//      small DAGs workflows tend to be (<20 nodes in practice).
//   3. Assign x = rank * 220, y = rowInRank * 80. Matches the frontend's
//      dagre defaults so the in-memory autorun on load is a no-op.
//   4. For each edge, pick source/target handle ids (`s-right`, `t-left`,
//      etc.) based on the dx/dy between final positions. See
//      `frontend/src/lib/workflow/autoLayout.ts` for the matching rules.
//
// We round-trip through JsonElement so the controller layer stays
// oblivious — node/edge objects are stored as raw JSON in Postgres
// (jsonb) and this helper preserves every property it doesn't manage
// (config_overrides, type, condition, etc.).
public static class WorkflowAutoLayout
{
    private const int RankSep = 220;
    private const int RowSep = 80;

    // Run layout only when the payload actually benefits from it. Two
    // signals: the agent never sets edge handles (so any missing handle
    // means "came from the agent / import"), and "everyone at 0/0" is the
    // other telltale. A user-tuned workflow has both distinct positions
    // and handles set — we skip it and leave their work alone.
    public static bool NeedsLayout(JsonElement nodes, JsonElement edges)
    {
        if (edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object) continue;
                var hasSource = edge.TryGetProperty("source_handle", out var sh)
                    && sh.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(sh.GetString());
                var hasTarget = edge.TryGetProperty("target_handle", out var th)
                    && th.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(th.GetString());
                if (!hasSource || !hasTarget) return true;
            }
        }

        if (nodes.ValueKind != JsonValueKind.Array) return false;
        var taskXs = new HashSet<double>();
        var taskYs = new HashSet<double>();
        var taskCount = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(node, "id");
            if (id is "__start__" or "__end__") continue;
            taskCount++;
            taskXs.Add(ReadDouble(node, "x"));
            taskYs.Add(ReadDouble(node, "y"));
        }
        if (taskCount <= 1) return false;
        if (taskXs.Count == 1 && taskYs.Count == 1) return true;
        return false;
    }

    public static (JsonElement nodes, JsonElement edges) Apply(JsonElement nodes, JsonElement edges)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return (nodes, edges);

        // Extract lightweight layout records — we only need id + whether
        // it's a sentinel for the algo; everything else passes through
        // verbatim when we rewrite the JSON.
        var nodeIds = new List<string>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(n, "id");
            if (string.IsNullOrEmpty(id)) continue;
            if (indexById.ContainsKey(id!)) continue;
            indexById[id!] = nodeIds.Count;
            nodeIds.Add(id!);
        }
        if (nodeIds.Count == 0) return (nodes, edges);

        // Build adjacency: predecessors + successors per node.
        var preds = new List<List<int>>(nodeIds.Count);
        var succs = new List<List<int>>(nodeIds.Count);
        for (var i = 0; i < nodeIds.Count; i++)
        {
            preds.Add(new List<int>());
            succs.Add(new List<int>());
        }
        if (edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in edges.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var src = ReadString(e, "source");
                var tgt = ReadString(e, "target");
                if (src is null || tgt is null) continue;
                if (!indexById.TryGetValue(src, out var si)) continue;
                if (!indexById.TryGetValue(tgt, out var ti)) continue;
                if (si == ti) continue;
                succs[si].Add(ti);
                preds[ti].Add(si);
            }
        }

        var ranks = ComputeRanks(preds, succs);
        var positions = AssignPositions(ranks, preds);

        var newNodes = RewriteNodes(nodes, indexById, positions);
        var newEdges = RewriteEdges(edges, indexById, positions);
        return (newNodes, newEdges);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Layout primitives
    // ─────────────────────────────────────────────────────────────────

    // Longest-path ranking: rank[v] = max over preds (rank[u] + 1), with
    // source nodes (no preds) at 0. Iterative until stable — handles the
    // DAG traversal order without needing an explicit topological sort.
    private static int[] ComputeRanks(List<List<int>> preds, List<List<int>> succs)
    {
        var ranks = new int[preds.Count];
        bool changed;
        var guard = 0;
        do
        {
            changed = false;
            for (var i = 0; i < ranks.Length; i++)
            {
                var max = preds[i].Count == 0 ? 0 : preds[i].Max(p => ranks[p] + 1);
                if (max > ranks[i])
                {
                    ranks[i] = max;
                    changed = true;
                }
            }
            guard++;
        } while (changed && guard < ranks.Length + 4);
        return ranks;
    }

    // Within each rank we order nodes by the barycenter of their
    // predecessors' row positions. Two passes is enough for the DAG
    // sizes workflows tend to be; pretty diagrams win over optimal
    // crossings here. Output: per-node (x, y) in pixels.
    private static (double X, double Y)[] AssignPositions(int[] ranks, List<List<int>> preds)
    {
        var byRank = new Dictionary<int, List<int>>();
        for (var i = 0; i < ranks.Length; i++)
        {
            if (!byRank.TryGetValue(ranks[i], out var list))
            {
                list = new List<int>();
                byRank[ranks[i]] = list;
            }
            list.Add(i);
        }

        // Initial order inside each rank = insertion order (stable).
        var rowById = new double[ranks.Length];
        foreach (var rankList in byRank.Values)
        {
            for (var j = 0; j < rankList.Count; j++)
                rowById[rankList[j]] = j;
        }

        // Barycenter passes. Recompute per-rank order using the mean of
        // predecessor rows; leaves roots alone.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (rank, rankList) in byRank.OrderBy(kv => kv.Key))
            {
                if (rank == 0) continue;
                rankList.Sort((a, b) =>
                {
                    var ba = preds[a].Count == 0 ? rowById[a] : preds[a].Average(p => rowById[p]);
                    var bb = preds[b].Count == 0 ? rowById[b] : preds[b].Average(p => rowById[p]);
                    return ba.CompareTo(bb);
                });
                for (var j = 0; j < rankList.Count; j++)
                    rowById[rankList[j]] = j;
            }
        }

        var positions = new (double X, double Y)[ranks.Length];
        foreach (var (rank, rankList) in byRank)
        {
            // Center each rank around y=0 so the diagram doesn't drift
            // downward — easier to read at a glance.
            var offset = (rankList.Count - 1) / 2.0;
            for (var j = 0; j < rankList.Count; j++)
            {
                var i = rankList[j];
                positions[i] = (rank * RankSep, (j - offset) * RowSep);
            }
        }
        return positions;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Handle selection — mirrors frontend/src/lib/workflow/autoLayout.ts
    // ─────────────────────────────────────────────────────────────────

    private static (string SourceHandle, string TargetHandle) PickHandles(
        double sourceX, double sourceY, double targetX, double targetY)
    {
        var dx = targetX - sourceX;
        var dy = targetY - sourceY;
        if (dx > 0) return ("s-right", "t-left");
        if (dx < 0) return ("s-bottom", "t-bottom");
        return dy >= 0 ? ("s-bottom", "t-top") : ("s-top", "t-bottom");
    }

    // ─────────────────────────────────────────────────────────────────
    //  JSON rewrite — preserve every property we don't manage
    // ─────────────────────────────────────────────────────────────────

    private static JsonElement RewriteNodes(
        JsonElement nodes,
        Dictionary<string, int> indexById,
        (double X, double Y)[] positions)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    node.WriteTo(writer);
                    continue;
                }

                var id = ReadString(node, "id");
                if (id is null || !indexById.TryGetValue(id, out var idx))
                {
                    node.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();
                var wroteX = false;
                var wroteY = false;
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("x"))
                    {
                        writer.WriteNumber("x", Math.Round(positions[idx].X));
                        wroteX = true;
                        continue;
                    }
                    if (prop.NameEquals("y"))
                    {
                        writer.WriteNumber("y", Math.Round(positions[idx].Y));
                        wroteY = true;
                        continue;
                    }
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                if (!wroteX) writer.WriteNumber("x", Math.Round(positions[idx].X));
                if (!wroteY) writer.WriteNumber("y", Math.Round(positions[idx].Y));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    private static JsonElement RewriteEdges(
        JsonElement edges,
        Dictionary<string, int> indexById,
        (double X, double Y)[] positions)
    {
        if (edges.ValueKind != JsonValueKind.Array) return edges;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object)
                {
                    edge.WriteTo(writer);
                    continue;
                }

                var src = ReadString(edge, "source");
                var tgt = ReadString(edge, "target");
                if (src is null || tgt is null
                    || !indexById.TryGetValue(src, out var si)
                    || !indexById.TryGetValue(tgt, out var ti))
                {
                    edge.WriteTo(writer);
                    continue;
                }

                var (sh, th) = PickHandles(
                    positions[si].X, positions[si].Y,
                    positions[ti].X, positions[ti].Y);

                writer.WriteStartObject();
                var wroteSh = false;
                var wroteTh = false;
                foreach (var prop in edge.EnumerateObject())
                {
                    if (prop.NameEquals("source_handle"))
                    {
                        writer.WriteString("source_handle", sh);
                        wroteSh = true;
                        continue;
                    }
                    if (prop.NameEquals("target_handle"))
                    {
                        writer.WriteString("target_handle", th);
                        wroteTh = true;
                        continue;
                    }
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
                if (!wroteSh) writer.WriteString("source_handle", sh);
                if (!wroteTh) writer.WriteString("target_handle", th);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }

    // ─────────────────────────────────────────────────────────────────
    //  JSON helpers
    // ─────────────────────────────────────────────────────────────────

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double ReadDouble(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => 0,
        };
    }
}
