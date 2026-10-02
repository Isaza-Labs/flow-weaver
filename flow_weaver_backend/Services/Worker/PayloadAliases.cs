using System.Text.Json;

namespace flow_weaver_backend.Services.Worker;

/// <summary>
/// Rewrites a step's resolved payload from the accepted ALIAS spelling of a
/// key to the canonical one (snippets/SPEC.md — "Flow Weaver is the oracle for
/// payload keys").
/// </summary>
/// <remarks>
/// <para>
/// The interchange standard fixes one canonical key per thing and lists the
/// other product's spelling as an accepted alias. A bundle imports fine either
/// way — the graph is valid, the snippet exists, the dependencies resolve —
/// and then the first run fails, because the handler read <c>tool_name</c>
/// while the node said <c>tool</c>. That is the worst failure mode the format
/// has: it looks like it worked.
/// </para>
/// <para>
/// So the translation happens once, here, at the top of every handler, rather
/// than as a dozen <c>TryGetProperty</c> fallbacks scattered through them.
/// One list of aliases, one place to read when the contract moves.
/// </para>
/// <para>
/// The rules are deliberately conservative: an alias is used only when the
/// canonical key is ABSENT (a node carrying both is a FlowWeaver node with a
/// stray key, and the canonical one is what it means), the value is copied
/// verbatim, and every key this class does not know is passed through
/// untouched — handlers ignore what they do not read, and the importer already
/// notes unknown keys per node so a typo is visible before the first run.
/// </para>
/// <para>
/// Aliases that need to look something up in this instance's inventory — ssh's
/// <c>device</c>, ping's <c>device</c>, git's <c>repository</c> — are NOT here:
/// they resolve against the database and belong in the handler that already
/// holds the repository. This class is pure.
/// </para>
/// </remarks>
public static class PayloadAliases
{
    /// <summary>Alias → canonical, per snippet type. Applied only when the canonical key is absent.</summary>
    private static readonly Dictionary<string, (string From, string To)[]> Renames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // path parameters and query string (snippets/SPEC.md `integration_action`)
            ["integration_action"] = [("path_params", "params"), ("query_params", "query")],
            // the tool to call on the MCP server (`mcp_call`). `tool` is the
            // CANONICAL key and `tool_name` this product's older spelling —
            // written the other way round, a node carrying both would keep the
            // alias and discard the canonical key, i.e. call a different tool.
            ["mcp_call"] = [("tool_name", "tool")],
            // structured parsing and privileged mode (`ssh`)
            ["ssh"] = [("structured", "use_structured"), ("enable", "enable_secret")],
            // a single device, a literal address, or a list of either, is a
            // host list (`ansible_playbook`)
            ["ansible_playbook"] = [("device", "hosts"), ("targets", "hosts"), ("host", "hosts")],
        };

    /// <summary>
    /// Keys that are another product's extension and mean nothing here. They
    /// are dropped rather than passed through so they cannot be mistaken for
    /// an unknown FlowWeaver key later in the pipeline.
    /// </summary>
    /// <remarks>
    /// Empty today, and kept for the next genuinely foreign key.
    ///
    /// `slack_message`'s `via` used to be here, on the reasoning that this product posts with
    /// the deployment's bot token so the key is inert. It came out because inert is not the
    /// same as absent: snippets/SPEC.md says an unrecognised key survives normalisation
    /// untouched, and a key silently deleted is indistinguishable from one the author never
    /// wrote. It now reaches the handler, which ignores it, and the pipeline's unknown-key
    /// reporting says so out loud — which is the useful outcome and the one silence denied.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Drops =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Alias → canonical, for pairs where the alias is NOT rewritten when it stands alone but
    /// IS dropped when the canonical key is beside it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Renames"/> on purpose, and the separation is the whole point.
    /// A rename says "these two keys mean the same thing"; this says only "when both are here,
    /// the canonical one is the answer".
    ///
    /// `ping`'s `device` is the case that forces the distinction. It is not a spelling of
    /// `host` — it is an INVENTORY NAME the handler resolves against the device table, and
    /// renaming it to `host` would hand an inventory name straight to the prober, where a
    /// permissive resolver answers with whatever it likes. So `device` must survive on its
    /// own, and must lose to `host`.
    ///
    /// Both handlers already resolve their pair this way (`PingHandler` reads `device` only
    /// when `host` is blank; `SshHandler.ResolveCommands` drops `command` when `commands` is
    /// present, citing bundle/SPEC.md §4). Declaring it here does not change what they do — it
    /// moves the decision out of fourteen handlers, each of which is otherwise a chance to
    /// decide it differently. The ssh pair shows what "differently" would cost: a
    /// `non_reversible` step running `reload` where the author wrote `show run`.
    ///
    /// The handler-side guards STAY. A handler can be reached with a payload that never passed
    /// through here, and belt-and-braces is the right posture for a rule whose failure mode is
    /// running the wrong command on a device.
    /// </remarks>
    private static readonly Dictionary<string, (string Alias, string Canonical)[]> Redundant =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ping"] = [("device", "host")],
            ["ssh"] = [("command", "commands")],
        };

    public const string TransformType = "transform";
    public const string ReportType = "report";
    public const string RestCallType = "rest_call";

    /// <summary>The catalogued <c>rest_call</c> form's keys (snippets/SPEC.md `rest_call`).</summary>
    public static readonly string[] CataloguedRestCallKeys =
        ["source", "operation_id", "path_params", "query_params"];

    /// <summary>
    /// The payload with every alias this build knows rewritten to its
    /// canonical spelling. Returns the input untouched when there is nothing
    /// to rewrite — the common case, and the one that must not allocate.
    /// </summary>
    public static JsonElement Normalize(string? snippetType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return payload;

        var type = (snippetType ?? string.Empty).Trim();
        if (type.Length == 0) return payload;

        var renames = Renames.TryGetValue(type, out var r) ? r : [];
        var drops = Drops.TryGetValue(type, out var d) ? d : [];
        var redundant = Redundant.TryGetValue(type, out var rd) ? rd : [];
        var isTransform = Is(type, TransformType);
        var isReport = Is(type, ReportType);

        if (!NeedsRewrite(payload, renames, drops, isTransform, isReport)
            && !HasRedundantAlias(payload, redundant)) return payload;

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in payload.EnumerateObject())
            {
                if (drops.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;

                // A leftover, not an override: the canonical key beside it is the answer.
                if (IsRedundant(payload, redundant, prop.Name)) continue;

                // `mapping` is the older-bundle spelling of a JMESPath multiselect
                // hash: `{ "<out>": "<path>" }` IS `{out: path}`.
                if (isTransform && Is(prop.Name, "mapping"))
                {
                    if (!payload.TryGetProperty("expression", out _) && written.Add("expression")
                        && MultiselectHash(prop.Value) is { } expression)
                        writer.WriteString("expression", expression);
                    continue;
                }

                // `content` is a markdown string; this product's report takes a
                // structured document. One section carrying the markdown is the
                // equivalent the standard names.
                if (isReport && Is(prop.Name, "content"))
                {
                    if (!payload.TryGetProperty("document", out _) && written.Add("document"))
                    {
                        writer.WritePropertyName("document");
                        WriteMarkdownDocument(writer, payload, prop.Value);
                    }
                    continue;
                }

                var canonical = CanonicalOf(renames, prop.Name);
                if (canonical is not null)
                {
                    // A node carrying BOTH spellings means the canonical one:
                    // the alias is a leftover, not an override.
                    if (!payload.TryGetProperty(canonical, out _) && written.Add(canonical))
                    {
                        writer.WritePropertyName(canonical);
                        prop.Value.WriteTo(writer);
                    }
                    continue;
                }

                written.Add(prop.Name);
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static bool IsRedundant(
        JsonElement payload, (string Alias, string Canonical)[] redundant, string name)
    {
        foreach (var (alias, canonical) in redundant)
            if (string.Equals(name, alias, StringComparison.OrdinalIgnoreCase)
                && payload.TryGetProperty(canonical, out _))
                return true;
        return false;
    }

    private static bool HasRedundantAlias(
        JsonElement payload, (string Alias, string Canonical)[] redundant)
    {
        foreach (var (alias, canonical) in redundant)
            if (payload.TryGetProperty(alias, out _) && payload.TryGetProperty(canonical, out _))
                return true;
        return false;
    }

    /// <summary>
    /// The catalogued-form keys present on a <c>rest_call</c> payload that has
    /// no <c>url</c>. Empty when the node is the raw form this product runs.
    /// </summary>
    public static IReadOnlyList<string> CataloguedRestCall(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return [];
        if (payload.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
            return [];
        // `source` / `operation_id` are the discriminator; the two *_params
        // keys are listed too so the error names everything the author wrote.
        if (!payload.TryGetProperty("source", out _) && !payload.TryGetProperty("operation_id", out _))
            return [];
        return CataloguedRestCallKeys.Where(k => payload.TryGetProperty(k, out _)).ToList();
    }

    /// <summary>
    /// The refusal for a catalogued <c>rest_call</c>. This product has no REST
    /// operation catalogue for workflow steps — the equivalent is an
    /// <c>integration_action</c> node — so the step fails naming the keys
    /// instead of reporting "url is required", which is true but useless.
    /// </summary>
    public static string CataloguedRestCallError(IReadOnlyList<string> keys)
        => $"not_supported: this step uses the catalogued rest_call form ({string.Join(", ", keys)}), "
           + "which this instance does not implement. Rewrite it as the raw form (url, method, headers, "
           + "body, query) or as an integration_action node pointing at the registered integration.";

    // ── helpers ─────────────────────────────────────────────────────────

    private static bool NeedsRewrite(
        JsonElement payload, (string From, string To)[] renames, string[] drops,
        bool isTransform, bool isReport)
    {
        foreach (var prop in payload.EnumerateObject())
        {
            if (drops.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) return true;
            if (CanonicalOf(renames, prop.Name) is not null) return true;
            if (isTransform && Is(prop.Name, "mapping")) return true;
            if (isReport && Is(prop.Name, "content")) return true;
        }
        return false;
    }

    private static string? CanonicalOf((string From, string To)[] renames, string name)
    {
        foreach (var (from, to) in renames)
            if (Is(name, from)) return to;
        return null;
    }

    /// <summary>
    /// <c>{ "a": "b.c" }</c> → <c>{a: b.c}</c>: the JMESPath multiselect hash
    /// the mapping is shorthand for. A key that is not a bare identifier is
    /// quoted; a non-string value is a JMESPath literal, not a path.
    /// </summary>
    private static string? MultiselectHash(JsonElement mapping)
    {
        if (mapping.ValueKind != JsonValueKind.Object) return null;

        var parts = new List<string>();
        foreach (var prop in mapping.EnumerateObject())
        {
            var key = IsBareIdentifier(prop.Name) ? prop.Name : JsonSerializer.Serialize(prop.Name);
            string value;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                value = prop.Value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value)) continue;
            }
            else
            {
                value = "`" + prop.Value.GetRawText() + "`";
            }
            parts.Add($"{key}: {value}");
        }

        // JMESPath has no empty multiselect hash; nothing to select means
        // there is no expression to write, and the handler's own default
        // (the whole payload) is the honest answer.
        return parts.Count == 0 ? null : "{" + string.Join(", ", parts) + "}";
    }

    private static bool IsBareIdentifier(string name)
    {
        if (name.Length == 0) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private static void WriteMarkdownDocument(Utf8JsonWriter writer, JsonElement payload, JsonElement content)
    {
        var markdown = content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : content.GetRawText();
        var title = payload.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;

        writer.WriteStartObject();
        writer.WriteString("title", title);
        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("title", string.Empty);
        // The exporters render text, not markdown: the markup travels as
        // written so nothing is lost, but it is not typeset.
        writer.WriteString("description", markdown);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
