using YamlDotNet.RepresentationModel;

namespace flow_weaver_backend.Services.Ai.Tools;

// Shared OpenAPI-YAML slicer for operation_detail and discover_operations'
// include_details mode. Returns a condensed surface — parameter list +
// first-level request body properties + short response description — enough for
// the LLM to fill an execute_operation call without blowing the token budget on
// big specs.
//
// Local references are resolved. Specs generated from a vendor's own docs (the
// official Action1 one, NetBox's) define every parameter, body schema and
// response once under `components` and point at it with `$ref`; this slicer
// used to read the `$ref` entry as a parameter with no name and show such
// operations with `parameters: []`. The model then did not know that `limit`
// and `from` existed, took the server's 50-row default, and reported the first
// page as the whole inventory. `allOf` is flattened one level for the same
// reason: a ResultPage response is `allOf: [$ref ResultPage] + properties`.
public static class OperationYamlSlicer
{
    public sealed record DetailSlice(List<object> Parameters, object? RequestBody, string? ResponsePreview);

    public static DetailSlice ExtractDetail(string yaml, string method, string path)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new DetailSlice([], null, null);

        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths
            || !paths.Children.TryGetValue(new YamlScalarNode(path), out var pathNode)
            || pathNode is not YamlMappingNode methods
            || !methods.Children.TryGetValue(new YamlScalarNode(method.ToLowerInvariant()), out var opNode)
            || opNode is not YamlMappingNode op)
            return new DetailSlice([], null, null);

        var refs = new RefResolver(
            root.Children.TryGetValue(new YamlScalarNode("components"), out var c) && c is YamlMappingNode cm ? cm : null);

        return new DetailSlice(
            ExtractParameters(op, methods, refs),
            ExtractRequestBody(op, refs),
            ExtractResponsePreview(op, refs));
    }

    // Follows `$ref` chains of the form "#/components/<section>/<name>" against the
    // document's own components. Anything else (external files, other pointers)
    // is left as it is. Bounded so a self-referencing spec cannot loop.
    private sealed class RefResolver(YamlMappingNode? components)
    {
        private const int MaxDepth = 8;

        public YamlMappingNode Resolve(YamlMappingNode node)
        {
            var depth = 0;
            while (GetScalar(node, "$ref") is { } reference && depth++ < MaxDepth)
            {
                var target = Lookup(reference);
                if (target is null) break;
                node = target;
            }
            return node;
        }

        private YamlMappingNode? Lookup(string reference)
        {
            const string prefix = "#/components/";
            if (components is null || !reference.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var parts = reference[prefix.Length..].Split('/');
            if (parts.Length != 2) return null;
            if (!components.Children.TryGetValue(new YamlScalarNode(parts[0]), out var section)
                || section is not YamlMappingNode sec)
                return null;
            var key = parts[1].Replace("~1", "/").Replace("~0", "~");
            return sec.Children.TryGetValue(new YamlScalarNode(key), out var target) && target is YamlMappingNode tm
                ? tm
                : null;
        }
    }

    // Parameters can live on the op itself or on the shared path node; both are
    // gathered and deduped by (name, in) so the agent sees one entry per parameter.
    private static List<object> ExtractParameters(YamlMappingNode op, YamlMappingNode pathNode, RefResolver refs)
    {
        var results = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Harvest(YamlMappingNode scope)
        {
            if (!scope.Children.TryGetValue(new YamlScalarNode("parameters"), out var paramsNode)
                || paramsNode is not YamlSequenceNode paramsSeq)
                return;
            foreach (var raw in paramsSeq.OfType<YamlMappingNode>())
            {
                var entry = refs.Resolve(raw);
                var name = GetScalar(entry, "name") ?? string.Empty;
                var @in = GetScalar(entry, "in") ?? string.Empty;
                if (name.Length == 0 && @in.Length == 0) continue; // an unresolvable $ref, not a parameter
                if (!seen.Add($"{name}|{@in}")) continue;
                results.Add(new
                {
                    name,
                    @in,
                    required = GetBool(entry, "required") ?? string.Equals(@in, "path", StringComparison.OrdinalIgnoreCase),
                    description = GetScalar(entry, "description") ?? string.Empty,
                    schema = ExtractSchemaSummary(entry, refs),
                });
            }
        }

        Harvest(pathNode);
        Harvest(op);
        return results;
    }

    private static object? ExtractRequestBody(YamlMappingNode op, RefResolver refs)
    {
        if (!op.Children.TryGetValue(new YamlScalarNode("requestBody"), out var rb) || rb is not YamlMappingNode rbRaw) return null;
        var rbMap = refs.Resolve(rbRaw);
        if (!rbMap.Children.TryGetValue(new YamlScalarNode("content"), out var content) || content is not YamlMappingNode contentMap) return null;

        var jsonEntry = contentMap
            .OfType<KeyValuePair<YamlNode, YamlNode>>()
            .FirstOrDefault(kv => kv.Key is YamlScalarNode k && (k.Value ?? "").Contains("json", StringComparison.OrdinalIgnoreCase));
        if (jsonEntry.Value is not YamlMappingNode jsonMap) return null;

        return new
        {
            content_type = jsonEntry.Key is YamlScalarNode ck ? ck.Value : "application/json",
            required = GetBool(rbMap, "required") ?? false,
            schema = ExtractSchemaSummary(jsonMap, refs),
        };
    }

    private static string? ExtractResponsePreview(YamlMappingNode op, RefResolver refs)
    {
        if (!op.Children.TryGetValue(new YamlScalarNode("responses"), out var rs) || rs is not YamlMappingNode rsMap) return null;
        foreach (var code in new[] { "200", "201", "204", "default" })
        {
            if (rsMap.Children.TryGetValue(new YamlScalarNode(code), out var rNode) && rNode is YamlMappingNode rRaw)
                return GetScalar(refs.Resolve(rRaw), "description");
        }
        return null;
    }

    // Condensed schema summary: type, enum and the first level of properties, with
    // `$ref` followed and `allOf` flattened one level. Never recurses into nested
    // objects — the full schema would dominate the token budget on big APIs.
    private static object ExtractSchemaSummary(YamlMappingNode node, RefResolver refs)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode("schema"), out var sNode) || sNode is not YamlMappingNode sRaw)
            return new { };
        var s = refs.Resolve(sRaw);

        var type = GetScalar(s, "type");
        var properties = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(YamlMappingNode schema)
        {
            if (schema.Children.TryGetValue(new YamlScalarNode("properties"), out var propsNode) && propsNode is YamlMappingNode props)
                foreach (var kv in props.OfType<KeyValuePair<YamlNode, YamlNode>>())
                {
                    if (kv.Key is not YamlScalarNode key || key.Value is null || !seen.Add(key.Value)) continue;
                    properties.Add(new
                    {
                        name = key.Value,
                        type = kv.Value is YamlMappingNode vm ? GetScalar(refs.Resolve(vm), "type") : null,
                    });
                }
        }

        if (s.Children.TryGetValue(new YamlScalarNode("allOf"), out var allOf) && allOf is YamlSequenceNode parts)
            foreach (var part in parts.OfType<YamlMappingNode>())
            {
                var resolved = refs.Resolve(part);
                type ??= GetScalar(resolved, "type");
                Collect(resolved);
            }
        Collect(s);

        return new { type, @enum = GetStringList(s, "enum"), properties };
    }

    private static string? GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static bool? GetBool(YamlMappingNode node, string key)
    {
        var v = GetScalar(node, key);
        return v is null ? null : bool.TryParse(v, out var b) ? b : null;
    }

    private static List<string> GetStringList(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq) return [];
        return seq.OfType<YamlScalarNode>().Select(x => x.Value ?? string.Empty).Where(x => x.Length > 0).ToList();
    }
}
