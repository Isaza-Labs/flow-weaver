using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// Computes a bundle's <c>requires</c> block (bundle/SPEC.md §2) from what the
/// bundle can see — the same computation serves the exporter (declare it) and
/// the reader of a v2 bundle (infer it, §2.3).
/// </summary>
/// <remarks>
/// A capability is derived, never asserted: <c>subflow</c> because a node is a
/// subflow node, <c>template_filters</c> because a template actually pipes
/// through a filter, <c>per_device_scope</c> because a per_device consumer
/// reads a per_device producer by field. That is what makes the declaration
/// trustworthy on the far side — it cannot drift from the graph.
/// </remarks>
public static class BundleRequirements
{
    // `{{ … | … }}` — a pipe inside a template is a filter (templates/SPEC.md §4).
    private static readonly Regex TemplateWithFilter =
        new(@"\{\{[^}]*\|[^}]*\}\}", RegexOptions.Compiled);

    private static readonly Regex RunNamespace =
        new(@"\{\{\s*run\.", RegexOptions.Compiled);

    // `{{ steps.<node>.output.<field…> }}` — a by-field read of a step output.
    private static readonly Regex StepOutputByField =
        new(@"\{\{\s*steps\.([\w-]+)\.output\.", RegexOptions.Compiled);

    private static readonly Regex SecretRef =
        new(@"\$\{secret:[^}]+\}", RegexOptions.Compiled);

    public static BundleRequires Compute(WorkflowBundle bundle)
    {
        var snippetById = bundle.Dependencies.Snippets.ToDictionary(s => s.Id);
        var graphs = new List<(JsonElement Nodes, JsonElement Edges)> { (bundle.Nodes, bundle.Edges) };
        graphs.AddRange(bundle.Dependencies.Workflows.Select(w => (w.Nodes, w.Edges)));

        var capabilities = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (nodes, edges) in graphs)
        {
            if (nodes.ValueKind == JsonValueKind.Array)
            {
                var snippetByNode = NodeReferences.SnippetIdByNode(nodes);
                foreach (var node in nodes.EnumerateArray())
                {
                    if (NodeReferences.IsSubflowNode(node)) capabilities.Add(BundleCapabilities.Subflow);

                    if (!node.TryGetProperty("config_overrides", out var overrides)) continue;
                    var text = CollectStrings(overrides);
                    if (TemplateWithFilter.IsMatch(text)) capabilities.Add(BundleCapabilities.TemplateFilters);
                    if (RunNamespace.IsMatch(text)) capabilities.Add(BundleCapabilities.RunNamespace);

                    var nodeId = node.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    // The catalogued rest_call form is a capability, not a
                    // stylistic choice: an instance without a REST operation
                    // catalogue cannot run the node at all (snippets/SPEC.md
                    // `rest_call`, bundle/SPEC.md §2.2). Derived from the node,
                    // like every other capability, so it cannot drift.
                    if (nodeId is not null
                        && snippetByNode.TryGetValue(nodeId, out var restSid)
                        && snippetById.TryGetValue(restSid, out var restSnippet)
                        && string.Equals(restSnippet.Type, "rest_call", StringComparison.OrdinalIgnoreCase)
                        && SnippetKeyCatalog.IsCataloguedRestCall(overrides))
                        capabilities.Add(BundleCapabilities.RestCatalog);

                    // per_device consumer reading a per_device producer by field.
                    if (nodeId is not null && IsPerDevice(snippetByNode, snippetById, nodeId))
                    {
                        foreach (Match m in StepOutputByField.Matches(text))
                        {
                            if (IsPerDevice(snippetByNode, snippetById, m.Groups[1].Value))
                            {
                                capabilities.Add(BundleCapabilities.PerDeviceScope);
                                break;
                            }
                        }
                    }
                }
            }

            if (edges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    if (edge.ValueKind != JsonValueKind.Object) continue;
                    if (edge.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        && string.Equals(t.GetString(), "conditional", StringComparison.OrdinalIgnoreCase))
                        capabilities.Add(BundleCapabilities.ConditionalEdges);
                    if (edge.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var cond = c.GetString() ?? string.Empty;
                        if (TemplateWithFilter.IsMatch(cond)) capabilities.Add(BundleCapabilities.TemplateFilters);
                        if (RunNamespace.IsMatch(cond)) capabilities.Add(BundleCapabilities.RunNamespace);
                    }
                }
            }
        }

        foreach (var s in bundle.Dependencies.Snippets)
        {
            if (s.MaxParallel > 1) capabilities.Add(BundleCapabilities.MaxParallel);
            if (string.Equals(s.TargetMode, "per_pool", StringComparison.OrdinalIgnoreCase))
                capabilities.Add(BundleCapabilities.PerPool);
            if (s.NetworkEnabled) capabilities.Add(BundleCapabilities.PythonNetwork);
        }

        if (bundle.Triggers.Count > 0) capabilities.Add(BundleCapabilities.Triggers);

        return new BundleRequires
        {
            SnippetTypes = bundle.Dependencies.Snippets
                .Select(s => s.Type)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
            Capabilities = capabilities.ToList(),
            Secrets = CollectSecrets(bundle),
        };
    }

    /// <summary>
    /// Every <c>${secret:…}</c> reference in any node's config_overrides (main
    /// and sub-workflows) or any snippet's code / input_schema, with what uses
    /// it. The reference is the whole of what travels.
    /// </summary>
    public static List<BundleSecretRef> CollectSecrets(WorkflowBundle bundle)
    {
        var byRef = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Add(string text, string user)
        {
            foreach (Match m in SecretRef.Matches(text))
            {
                if (!byRef.TryGetValue(m.Value, out var users))
                    byRef[m.Value] = users = new SortedSet<string>(StringComparer.Ordinal);
                users.Add(user);
            }
        }

        void ScanNodes(JsonElement nodes, string? prefix)
        {
            if (nodes.ValueKind != JsonValueKind.Array) return;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("config_overrides", out var overrides)) continue;
                var nodeId = node.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
                Add(CollectStrings(overrides), prefix is null ? nodeId : $"{prefix}/{nodeId}");
            }
        }

        ScanNodes(bundle.Nodes, null);
        foreach (var w in bundle.Dependencies.Workflows) ScanNodes(w.Nodes, w.Name);
        foreach (var s in bundle.Dependencies.Snippets)
        {
            var user = $"snippet:{s.Slug ?? s.Name}";
            if (!string.IsNullOrEmpty(s.Code)) Add(s.Code, user);
            if (s.InputSchema is { ValueKind: JsonValueKind.Object } schema) Add(CollectStrings(schema), user);
        }

        return byRef
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new BundleSecretRef { Ref = kv.Key, UsedBy = kv.Value.ToList() })
            .ToList();
    }

    private static bool IsPerDevice(
        IReadOnlyDictionary<string, Guid> snippetByNode,
        IReadOnlyDictionary<Guid, BundleSnippet> snippetById,
        string nodeId)
        => snippetByNode.TryGetValue(nodeId, out var sid)
           && snippetById.TryGetValue(sid, out var snippet)
           && string.Equals(snippet.TargetMode, "per_device", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every string value in a JSON tree, newline-joined, for regex scans.</summary>
    public static string CollectStrings(JsonElement el)
    {
        var sb = new StringBuilder();
        Walk(el, sb);
        return sb.ToString();

        static void Walk(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    sb.Append(e.GetString()).Append('\n');
                    break;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, sb);
                    break;
                case JsonValueKind.Array:
                    foreach (var i in e.EnumerateArray()) Walk(i, sb);
                    break;
            }
        }
    }
}

/// <summary>
/// Deterministic serialization of a JSON value: object keys sorted, no
/// whitespace. Two graphs that differ only in key order or formatting hash the
/// same, which is what "identical nodes+edges" has to mean when the JSON was
/// written by two different serializers (bundle/SPEC.md §5.4, §8).
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonElement el)
    {
        var sb = new StringBuilder();
        Write(el, sb);
        return sb.ToString();
    }

    public static string Sha256(JsonElement nodes, JsonElement edges)
    {
        var text = Serialize(nodes) + "\n" + Serialize(edges);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Write(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Write(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var i in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(i, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;
            default:
                sb.Append(el.GetRawText());
                break;
        }
    }
}

/// <summary>
/// The keys each snippet type reads (snippets/SPEC.md: canonical keys, their
/// aliases, and this product's extensions). An importer MUST note keys no
/// alias covers, per node, so a typo or another product's extension is visible
/// at import instead of at run time (snippets/SPEC.md rule 3).
/// </summary>
public static class SnippetKeyCatalog
{
    private static readonly Dictionary<string, HashSet<string>> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = Set("host", "device", "count", "timeout_ms", "port"),
        ["ssh"] = Set(
            "commands", "command", "device", "host", "credential", "credential_id",
            "username", "password", "private_key", "key_passphrase",
            "use_structured", "structured", "stop_on_error", "enable_secret", "enable",
            "timeout_seconds", "port", "device_type", "setup_commands", "read_until_pattern",
            "direct_exec", "preserve_ansi", "use_timing"),
        ["rest_call"] = Set(
            "url", "method", "headers", "body", "query",
            "source", "operation_id", "path_params", "query_params"),
        ["transform"] = Set("expression", "input", "language", "mapping"),
        ["integration_action"] = Set(
            "integration", "action", "integration_id", "action_id",
            "body", "params", "path_params", "query", "query_params"),
        ["mcp_call"] = Set("server", "mcp_server_id", "tool", "tool_name", "arguments"),
        ["git"] = Set(
            "operation", "repository", "repository_id", "path", "paths", "ref", "branch",
            "content", "commit_message", "push", "author_name", "author_email"),
        ["report"] = Set("format", "document", "content", "retain_days", "title"),
        ["email_send"] = Set(
            "to", "cc", "bcc", "subject", "body", "html", "from_address", "from_name",
            "reply_to", "attachments", "channel", "channel_id"),
        ["slack_message"] = Set("channel", "text", "thread_ts", "blocks", "via"),
        ["ansible_playbook"] = Set("hosts", "device", "host", "targets", "extra_vars", "timeout_seconds", "playbook"),
    };

    // Keys every type may carry: the engine reads them, not the handler.
    private static readonly HashSet<string> Universal = Set("input");

    /// <summary>
    /// Keys the standard defines for every type but that are NOT silent: an
    /// importer must say something about them (execution/SPEC.md §2). They are
    /// excluded from <see cref="UnknownKeys"/> — "no known key covers this" is
    /// the wrong thing to say about a key the contract names — and reported
    /// by their own note instead.
    /// </summary>
    public const string IdempotencyKey = "idempotency";

    /// <summary>
    /// The node-level change declaration (`workflow-v1/run-outcome`), and the sibling of
    /// <see cref="IdempotencyKey"/> — one says whether an action could be undone, the other
    /// whether anything was done.
    /// </summary>
    /// <remarks>
    /// Excluded from <see cref="UnknownKeys"/> for the same reason, and the note it used to
    /// produce was wrong twice over: it called a contract-named key a possible typo, and it
    /// said "the handler ignores them" when the worker READS this one — and fails the step
    /// when it is absent for a type that defers. Found by running an externally authored bundle
    /// through this product's import.
    /// </remarks>
    public const string ChangesKey = "changes";

    /// <summary>The <c>config_overrides.idempotency</c> tier this node declares, or null.</summary>
    public static string? DeclaredIdempotency(JsonElement overrides)
        => overrides.ValueKind == JsonValueKind.Object
           && overrides.TryGetProperty(IdempotencyKey, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keys on this node's config_overrides that no canonical key, alias or
    /// extension of its snippet type covers. Empty for types the catalogue
    /// does not describe (python_snippet reads whatever the script wants).
    /// </summary>
    public static IReadOnlyList<string> UnknownKeys(string snippetType, JsonElement overrides)
    {
        if (overrides.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!Known.TryGetValue(snippetType, out var known)) return Array.Empty<string>();
        return overrides.EnumerateObject()
            .Select(p => p.Name)
            .Where(k => !known.Contains(k)
                        && !Universal.Contains(k)
                        && !string.Equals(k, IdempotencyKey, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(k, ChangesKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The catalogued rest_call form (snippets/SPEC.md `rest_call`) is recognised by these keys.</summary>
    public static readonly string[] CataloguedRestCallKeys = ["source", "operation_id", "path_params", "query_params"];

    public static bool IsCataloguedRestCall(JsonElement overrides)
        => overrides.ValueKind == JsonValueKind.Object
           && (overrides.TryGetProperty("source", out _) || overrides.TryGetProperty("operation_id", out _));
}
