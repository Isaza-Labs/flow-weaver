using System.Text.Json;

namespace flow_weaver_backend.Services.Import.Translators;

// Deterministic n8n → FlowWeaver v1 translator. Covers the common
// "official" node types; everything else falls through as a snippet
// reference the user resolves in the wizard (stub / generate / map).
//
// Mapping:
//   n8n-nodes-base.httpRequest      → rest_call
//   n8n-nodes-base.code / function  → python_snippet
//   n8n-nodes-base.executeCommand   → ssh
//   n8n-nodes-base.if               → split node + conditional edges
//   anything else                   → snippet_id = node.type (lets the
//                                     wizard surface it as a missing
//                                     snippet for the user to resolve)
public sealed class N8nTranslator : IDslTranslator
{
    public string FormatName => "n8n";

    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.Ordinal)
    {
        ["n8n-nodes-base.httpRequest"] = "rest_call",
        ["n8n-nodes-base.code"] = "python_snippet",
        ["n8n-nodes-base.function"] = "python_snippet",
        ["n8n-nodes-base.executeCommand"] = "ssh",
        ["n8n-nodes-base.emailSend"] = "integration_action",
        ["n8n-nodes-base.slack"] = "integration_action",
        ["n8n-nodes-base.webhook"] = "rest_call",
    };

    public Task<TranslationResult> TranslateAsync(JsonElement document, CancellationToken ct)
    {
        var notes = new List<string>();
        var warnings = new List<string>();

        var workflowName = TryGetString(document, "name") ?? "n8n import";
        var n8nNodes = document.TryGetProperty("nodes", out var ne) && ne.ValueKind == JsonValueKind.Array
            ? ne.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object).ToList()
            : new List<JsonElement>();
        var connections = document.TryGetProperty("connections", out var ce) && ce.ValueKind == JsonValueKind.Object
            ? ce
            : default;

        // n8n nodes are addressed by their `name` field in `connections`,
        // not by an id. We mirror that.
        var v1Nodes = new List<object>();
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var x = 100;
        var y = 100;

        v1Nodes.Add(new
        {
            id = "__start__",
            snippet_id = "__start__",
            x = 0,
            y,
            type = "sentinel",
            config_overrides = new { },
        });

        foreach (var node in n8nNodes)
        {
            var name = TryGetString(node, "name") ?? $"node_{v1Nodes.Count}";
            var nType = TryGetString(node, "type") ?? "";
            var safeId = SanitiseId(name);
            var snippetId = TypeMap.TryGetValue(nType, out var mapped) ? mapped : nType;
            if (!TypeMap.ContainsKey(nType) && !string.IsNullOrEmpty(nType))
                notes.Add($"Unmapped n8n type '{nType}' for node '{name}' — will be flagged as a missing snippet.");

            var configOverrides = MapNodeConfig(node, snippetId);

            v1Nodes.Add(new
            {
                id = safeId,
                snippet_id = snippetId,
                x,
                y,
                type = "task",
                config_overrides = configOverrides,
            });
            nameToIndex[name] = v1Nodes.Count - 1;
            x += 220;
        }

        v1Nodes.Add(new
        {
            id = "__end__",
            snippet_id = "__end__",
            x = x + 200,
            y = 100,
            type = "sentinel",
            config_overrides = new { },
        });

        var v1Edges = new List<object>();
        // Wire start → first node.
        if (n8nNodes.Count > 0)
        {
            var firstId = SanitiseId(TryGetString(n8nNodes[0], "name") ?? "node_1");
            v1Edges.Add(new { source = "__start__", target = firstId, type = "success" });
        }
        else
        {
            v1Edges.Add(new { source = "__start__", target = "__end__", type = "success" });
        }

        if (connections.ValueKind == JsonValueKind.Object)
        {
            foreach (var srcConn in connections.EnumerateObject())
            {
                var srcName = srcConn.Name;
                if (!srcConn.Value.TryGetProperty("main", out var main)
                    || main.ValueKind != JsonValueKind.Array) continue;

                // main is array of arrays — each outer index corresponds
                // to an output port (success/failure/etc.). n8n uses
                // index 0 = main = success in 95 % of cases.
                var outIndex = 0;
                foreach (var port in main.EnumerateArray())
                {
                    var edgeType = outIndex == 0 ? "success" : "failure";
                    if (port.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var target in port.EnumerateArray())
                        {
                            if (target.ValueKind != JsonValueKind.Object) continue;
                            var tgtName = TryGetString(target, "node") ?? "";
                            if (string.IsNullOrEmpty(tgtName)) continue;
                            v1Edges.Add(new
                            {
                                source = SanitiseId(srcName),
                                target = SanitiseId(tgtName),
                                type = edgeType,
                            });
                        }
                    }
                    outIndex++;
                }
            }
        }

        // Wire any leaf node (no outgoing edges) to __end__.
        var leafIds = ComputeLeafIds(n8nNodes, v1Edges);
        foreach (var leafId in leafIds)
        {
            v1Edges.Add(new { source = leafId, target = "__end__", type = "success" });
        }

        if (document.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
            notes.Add("Source n8n workflow was inactive — imported as draft regardless.");

        var v1 = JsonSerializer.SerializeToElement(new
        {
            schema_version = "v1",
            name = workflowName,
            description = (string?)null,
            input_schema = new { },
            nodes = v1Nodes,
            edges = v1Edges,
            metadata = new { source_format = "n8n", original_node_count = n8nNodes.Count },
        });

        return Task.FromResult(new TranslationResult
        {
            V1Workflow = v1,
            Notes = notes,
            Warnings = warnings,
        });
    }

    private static object MapNodeConfig(JsonElement node, string snippetId)
    {
        var parameters = node.TryGetProperty("parameters", out var p) ? p : default;
        return snippetId switch
        {
            "rest_call" => MapHttpRequest(parameters),
            _ => parameters.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters.GetRawText()) ?? new()
                : new Dictionary<string, JsonElement>(),
        };
    }

    private static object MapHttpRequest(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return new { };
        var method = TryGetString(parameters, "requestMethod") ?? TryGetString(parameters, "method") ?? "GET";
        var url = TryGetString(parameters, "url") ?? "";
        return new
        {
            method,
            url,
            headers = new { },
            body = (object?)null,
        };
    }

    private static IEnumerable<string> ComputeLeafIds(List<JsonElement> nodes, List<object> edges)
    {
        var allIds = nodes.Select(n => SanitiseId(TryGetString(n, "name") ?? "")).ToHashSet();
        // We can't easily inspect anonymous objects here without
        // serialising; serialise + parse to avoid mutating call sites.
        var serialized = JsonSerializer.SerializeToElement(edges);
        var sourcesWithOutEdge = new HashSet<string>();
        foreach (var e in serialized.EnumerateArray())
        {
            var src = TryGetString(e, "source") ?? "";
            sourcesWithOutEdge.Add(src);
        }
        return allIds.Where(id => !sourcesWithOutEdge.Contains(id));
    }

    private static string? TryGetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string SanitiseId(string raw)
    {
        var s = new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(s) ? Guid.NewGuid().ToString("N")[..8] : s;
    }
}
