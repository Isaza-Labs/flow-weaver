using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace flow_weaver_backend.Services.Engine;

// Captured output of a single completed step, keyed by the step's node id
// in the VariableResolver's step map. Other execution metadata (duration,
// status) is tracked on step_runs — the resolver only cares about output.
public sealed record StepResult(JsonElement Output);

public interface IVariableResolver
{
    // Returns a new JsonElement where every template reference inside
    // string values has been replaced.
    //   - `completedSteps` is keyed by node id and feeds
    //     `{{ steps.X.output.Y }}` templates.
    //   - `deviceContext` is the current target device's public view
    //     (id/name/ip/platform/vendor/os_version/site/role/external_id/
    //      status/properties) and feeds `{{ device.X }}` templates.
    //     Pass `null` for steps without a single device context (once
    //     mode with multiple targets, subflow nodes, sentinels) — any
    //     `{{ device.X }}` reference in that case is left literal and
    //     logged.
    //   - `runInput` is the raw `input` payload the caller passed when
    //     triggering the run and feeds `{{ input.X }}` templates. Pass
    //     `null` when no input was provided — any `{{ input.X }}`
    //     reference in that case is left literal and logged.
    JsonElement Resolve(
        JsonElement payload,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null);
}

// Walks the payload tree and rewrites string values that match template
// references. Two template families are supported:
//   - `{{ steps.<node>.output(.path)? }}`   — upstream step output
//   - `{{ device(.path)? }}`                — current target device
//
// Two substitution modes:
//   - Whole-string template (e.g. "{{ steps.ping.output }}"): the string
//     is replaced by the referenced JsonElement in place. This preserves
//     types — object/array/number/bool values arrive at the executor
//     without a lossy toString round-trip.
//   - Inline template (e.g. "status=${{ steps.ping.output.rtt }}ms"):
//     each template occurrence is replaced by the string form of the
//     resolved value; primitives stringify naturally, object/array
//     render as their raw JSON text.
//
// Unresolvable templates (missing step, missing path segment, absent
// device context) are left as literal text. The executor logs them and
// continues — one bad reference should not poison an entire payload.
public partial class VariableResolver : IVariableResolver
{
    // `steps.<node_id>.output` optionally followed by a path expression.
    // Node ids can contain hyphens (the workflow schema only enforces
    // minLength/maxLength, not a pattern), so `make-report`, `send-email`
    // etc. are all valid. \w doesn't match `-`, so we add it explicitly;
    // without this, every hyphenated node id silently fails to resolve
    // and the template string reaches the handler unchanged.
    //
    // The path capture group is permissive on purpose: anything after
    // `.output` that is not whitespace or a closing `}` is part of the
    // path. This lets callers mix dotted access and array indexing:
    //   {{ steps.x.output.field }}
    //   {{ steps.x.output.devices[0].output.stdout }}
    //   {{ steps.x.output[2] }}
    // TryResolvePath parses the captured expression itself.
    // Captures the variable expression (path + optional `| filter` chain)
    // as group 2. The non-greedy `[^}]*?` allows internal whitespace and
    // `|` characters so `{{ steps.x.output.foo | default('-') }}` matches
    // — the parser splits group 2 into path + filters. `}` in filter
    // arguments is the one disallowed character; quote-aware parsing is
    // not worth the regex complexity for that corner case.
    [GeneratedRegex(@"\{\{\s*steps\.([\w-]+)\.output([^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex TemplateRegex();

    // `device` followed by an optional path. The `\b` after `device`
    // anchors on a word boundary so `{{ deviceName }}` or
    // `{{ device_id }}` don't accidentally match the device context
    // template. Accepts:
    //   {{ device }}                         → whole device object
    //   {{ device.name }}                    → `name` property
    //   {{ device.properties.foo[0].bar }}   → nested access
    [GeneratedRegex(@"\{\{\s*device(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex DeviceRegex();

    // `input` followed by an optional path — the trigger payload. Mirrors
    // DeviceRegex's `\b` anchor so `{{ inputs }}` or `{{ input_date }}`
    // don't accidentally match. The `input` context is whatever the caller
    // supplied under the `input` field when starting a run (stored on
    // WorkflowRun.InputPayload). Accepts:
    //   {{ input }}                          → whole input object
    //   {{ input.report_date }}              → `report_date` property
    //   {{ input.targets[0].id }}            → nested access
    [GeneratedRegex(@"\{\{\s*input(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex InputRegex();

    // `run` followed by an optional path — the current run's metadata.
    // Populated by WorkflowExecutor from the WorkflowRun + Workflow rows.
    // Available fields:
    //   {{ run.id }}                  WorkflowRun.WorkflowRunId
    //   {{ run.workflow_id }}         WorkflowRun.WorkflowId
    //   {{ run.workflow_name }}       Workflow.Name (frozen at enqueue)
    //   {{ run.environment }}         Workflow.Environment (draft|qa|production)
    //   {{ run.trigger }}             "manual" | "schedule" | "webhook" | "subflow"
    //   {{ run.started_at }}          ISO-8601 timestamp (string)
    //   {{ run.owner_email }}         email of the user who started the run
    //   {{ run.url }}                 link to the run page (Workflow:PublicBaseUrl)
    //   {{ run.failed_step_id }}      node id of the failing predecessor, or ""
    //   {{ run.failed_step_error }}   that step's error message, or ""
    // The two failed_step_* fields are what makes the canonical
    // notify-failure node useful; they're filled only when the step being
    // enqueued fires from a `failure` edge, and are empty strings otherwise
    // so an `always`-reached node still resolves them.
    // Mirrors InputRegex's `\b` anchor so `{{ runs }}` / `{{ runtime }}`
    // don't accidentally match.
    [GeneratedRegex(@"\{\{\s*run(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex RunRegex();

    // Keywords that cause the resolver to redact values in log output. The
    // variable key (node id + path expression) is matched case-insensitively.
    private static readonly string[] SecretKeywords =
        ["password", "token", "secret", "key", "credential"];

    private readonly ILogger<VariableResolver> _logger;

    public VariableResolver(ILogger<VariableResolver> logger)
    {
        _logger = logger;
    }

    public JsonElement Resolve(
        JsonElement payload,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteValue(writer, payload, completedSteps, deviceContext, runInput, runContext);
        }

        // Parse the rewritten JSON into a fresh JsonDocument. The JsonElement
        // holds the JsonDocument alive via its internal parent reference, so
        // callers can safely return it.
        var bytes = ms.ToArray();
        var doc = JsonDocument.Parse(bytes);
        return doc.RootElement;
    }

    // Broader regex than the `steps.X.output…` / `device.X` grammars above —
    // any `{{ … }}` shape counts as a residual because if the grammar-
    // matching resolver left it behind, it definitely didn't resolve.
    // Used by WorkflowExecutor as a post-resolve defensive scan before
    // a step_run's InputPayload gets persisted: a leftover template means
    // the author referenced a step that doesn't exist, a path that
    // doesn't exist, or typed the wrong grammar. In every case the step
    // would produce garbage output (the incident that drove this fix:
    // the CSV ended up with literal `{{ steps.dns-reconcile.output.rows[0] }}`
    // cells instead of real rows). Failing at enqueue time means the
    // handler never gets a chance to emit fiction.
    [GeneratedRegex(@"\{\{\s*[^}]+\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex ResidualTemplateRegex();

    // Represents one unresolved template found in an already-resolved payload.
    // `Location` is a dotted JSON path pointing at the offending value so
    // the error message can say *"$.document.sections[0].tables[0].rows[3][1]:
    // {{ steps.X.output.Y }}"* — good enough for the author to jump straight
    // to the broken field.
    public sealed record UnresolvedTemplate(string Location, string TemplateText);

    // Walks a resolved JsonElement and collects every string value that
    // still contains an unresolved `{{ … }}` template. Returns an empty
    // list when the payload is clean. Stateless + pure — callable from
    // WorkflowExecutor or any handler that wants belt-and-suspenders.
    public static IReadOnlyList<UnresolvedTemplate> FindUnresolvedTemplates(JsonElement payload)
    {
        var found = new List<UnresolvedTemplate>();
        WalkForResiduals(payload, path: "$", found);
        return found;
    }

    private static void WalkForResiduals(JsonElement el, string path, List<UnresolvedTemplate> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = el.GetString();
                if (string.IsNullOrEmpty(s)) return;
                var match = ResidualTemplateRegex().Match(s);
                if (match.Success)
                {
                    // Trim the captured template text so the error message
                    // stays short regardless of surrounding text.
                    sink.Add(new UnresolvedTemplate(path, match.Value.Trim()));
                }
                return;
            }
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    WalkForResiduals(prop.Value, $"{path}.{prop.Name}", sink);
                return;
            case JsonValueKind.Array:
            {
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    WalkForResiduals(item, $"{path}[{i++}]", sink);
                return;
            }
            default:
                return;
        }
    }

    private void WriteValue(
        Utf8JsonWriter writer,
        JsonElement el,
        IReadOnlyDictionary<string, StepResult> steps,
        JsonElement? deviceContext,
        JsonElement? runInput,
        JsonElement? runContext)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                WriteResolvedString(writer, el.GetString() ?? string.Empty, steps, deviceContext, runInput, runContext);
                break;

            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteValue(writer, prop.Value, steps, deviceContext, runInput, runContext);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    WriteValue(writer, item, steps, deviceContext, runInput, runContext);
                writer.WriteEndArray();
                break;

            default:
                // number, true, false, null, undefined — pass through
                el.WriteTo(writer);
                break;
        }
    }

    // Attempts whole-string replacement for the first template that spans
    // the entire `input`, preserving the referenced value's JSON type.
    // Returns `true` when the write succeeded (caller must stop); `false`
    // leaves the writer untouched and the caller proceeds to inline mode.
    private bool TryWriteWholeStringTemplate(
        Utf8JsonWriter writer,
        string input,
        IReadOnlyDictionary<string, StepResult> steps,
        JsonElement? deviceContext,
        JsonElement? runInput,
        JsonElement? runContext)
    {
        // Matched against the TRIMMED text: a JSON type must not depend on characters nobody
        // can see. `"  {{ x }}  "` is the same intent as `"{{ x }}"`, and treating the first
        // as interpolation-into-a-string turns a boolean into the text "   true   ".
        // The unresolved fallbacks below still write the ORIGINAL string, so a reference that
        // does not resolve stays literal exactly as the author typed it.
        var probe = input.Trim();

        var stepMatch = TemplateRegex().Match(probe);
        if (stepMatch.Success && stepMatch.Index == 0 && stepMatch.Length == probe.Length)
        {
            var nodeId = stepMatch.Groups[1].Value;
            var parsed = ParseExpression(stepMatch.Groups[2].Value);
            var varKey = nodeId + parsed.Path;
            JsonElement? resolved = null;
            if (steps.TryGetValue(nodeId, out var step) && TryResolvePath(step.Output, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                filtered.Value.WriteTo(writer);
                return true;
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            writer.WriteStringValue(input);
            return true;
        }

        var deviceMatch = DeviceRegex().Match(probe);
        if (deviceMatch.Success && deviceMatch.Index == 0 && deviceMatch.Length == probe.Length)
        {
            var parsed = ParseExpression(deviceMatch.Groups[1].Value);
            var varKey = "device" + parsed.Path;
            JsonElement? resolved = null;
            if (deviceContext.HasValue && TryResolvePath(deviceContext.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                filtered.Value.WriteTo(writer);
                return true;
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            writer.WriteStringValue(input);
            return true;
        }

        var inputMatch = InputRegex().Match(probe);
        if (inputMatch.Success && inputMatch.Index == 0 && inputMatch.Length == probe.Length)
        {
            var parsed = ParseExpression(inputMatch.Groups[1].Value);
            var varKey = "input" + parsed.Path;
            JsonElement? resolved = null;
            if (runInput.HasValue && TryResolvePath(runInput.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                filtered.Value.WriteTo(writer);
                return true;
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            writer.WriteStringValue(input);
            return true;
        }

        var runMatch = RunRegex().Match(probe);
        if (runMatch.Success && runMatch.Index == 0 && runMatch.Length == probe.Length)
        {
            var parsed = ParseExpression(runMatch.Groups[1].Value);
            var varKey = "run" + parsed.Path;
            JsonElement? resolved = null;
            if (runContext.HasValue && TryResolvePath(runContext.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                filtered.Value.WriteTo(writer);
                return true;
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            writer.WriteStringValue(input);
            return true;
        }

        return false;
    }

    private void WriteResolvedString(
        Utf8JsonWriter writer,
        string input,
        IReadOnlyDictionary<string, StepResult> steps,
        JsonElement? deviceContext,
        JsonElement? runInput,
        JsonElement? runContext)
    {
        // Fast path + type-preserving path: the string is ENTIRELY one
        // template (steps.*, device.*, input.*, or run.*). Replace with
        // the raw JsonElement so the payload keeps the referenced value's
        // type.
        if (TryWriteWholeStringTemplate(writer, input, steps, deviceContext, runInput, runContext))
            return;

        // Inline mode: stringify each template occurrence. Step templates
        // are substituted first, then device, then input — the order doesn't
        // matter because the three grammars are disjoint (the steps regex
        // requires a literal `steps.` prefix, device requires the word
        // `device`, input requires the word `input`).
        var replaced = TemplateRegex().Replace(input, m =>
        {
            var nodeId = m.Groups[1].Value;
            var parsed = ParseExpression(m.Groups[2].Value);
            var varKey = nodeId + parsed.Path;
            JsonElement? resolved = null;
            if (steps.TryGetValue(nodeId, out var step) && TryResolvePath(step.Output, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                return Stringify(filtered.Value);
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            return m.Value;
        });

        replaced = DeviceRegex().Replace(replaced, m =>
        {
            var parsed = ParseExpression(m.Groups[1].Value);
            var varKey = "device" + parsed.Path;
            JsonElement? resolved = null;
            if (deviceContext.HasValue && TryResolvePath(deviceContext.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                return Stringify(filtered.Value);
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            return m.Value;
        });

        replaced = InputRegex().Replace(replaced, m =>
        {
            var parsed = ParseExpression(m.Groups[1].Value);
            var varKey = "input" + parsed.Path;
            JsonElement? resolved = null;
            if (runInput.HasValue && TryResolvePath(runInput.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                return Stringify(filtered.Value);
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            return m.Value;
        });

        replaced = RunRegex().Replace(replaced, m =>
        {
            var parsed = ParseExpression(m.Groups[1].Value);
            var varKey = "run" + parsed.Path;
            JsonElement? resolved = null;
            if (runContext.HasValue && TryResolvePath(runContext.Value, parsed.Path, out var v))
                resolved = v;
            var filtered = ApplyFilters(resolved, parsed.Filters);
            if (filtered.HasValue)
            {
                LogResolveOk(varKey, filtered.Value);
                return Stringify(filtered.Value);
            }
            _logger.LogWarning("engine.variable.resolve.missing variable={Variable}", varKey);
            return m.Value;
        });

        writer.WriteStringValue(replaced);
    }

    private void LogResolveOk(string variableKey, JsonElement value)
    {
        if (IsSecretKey(variableKey))
        {
            _logger.LogDebug(
                "engine.variable.resolve.ok variable={Variable} value=<redacted>",
                variableKey);
            return;
        }

        var length = value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Length
            : value.GetRawText().Length;
        _logger.LogDebug(
            "engine.variable.resolve.ok variable={Variable} kind={Kind} length={Length}",
            variableKey, value.ValueKind, length);
    }

    private static bool IsSecretKey(string key)
    {
        foreach (var kw in SecretKeywords)
        {
            if (key.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Path grammar: a sequence of `.<propertyName>`, `[<arrayIndex>]`, and
    // `['<key>']` / `["<key>"]` segments, in any order. Examples:
    //   ".stdout"                                  → root.stdout
    //   ".devices[0].output.stdout"                → root.devices[0].output.stdout
    //   ".devices_by_name['router-1'].output"      → object key with hyphens
    //   "[2].status"                               → root[2].status
    // Quoted bracket access lets templates name keys that aren't valid
    // identifiers (hostnames with hyphens, dots, spaces) without resorting
    // to numeric indices.
    //
    // An empty path resolves to root itself. Returns false when any
    // segment can't be traversed (wrong kind, missing property, index
    // out of range, malformed bracket expression).
    //
    // `internal` so ConditionEvaluator reuses the exact same semantics —
    // both walk step outputs and must agree on what a template expression
    // resolves to (otherwise a condition might evaluate true against a path
    // the resolver fails to substitute, or vice versa).
    /// <summary>
    /// One reference — path plus filter chain — resolved against a root, or null when it does
    /// not resolve.
    /// </summary>
    /// <remarks>
    /// Public so `ConditionEvaluator` resolves a reference EXACTLY the way a payload does. Two
    /// grammars would let an author write a condition that reads correctly and evaluates
    /// against nothing, which is the shape of every fail-open bug in this area.
    /// </remarks>
    public static JsonElement? ResolveExpression(JsonElement? root, string expression)
    {
        var parsed = ParseExpression(expression);
        JsonElement? resolved = root is { } r && TryResolvePath(r, parsed.Path, out var v) ? v : null;
        return ApplyFilters(resolved, parsed.Filters);
    }

    internal static bool TryResolvePath(JsonElement root, string pathExpr, out JsonElement result)
    {
        result = root;
        if (string.IsNullOrEmpty(pathExpr))
            return true;

        var current = root;
        int i = 0;
        while (i < pathExpr.Length)
        {
            var c = pathExpr[i];
            if (c == '.')
            {
                i++;
                var start = i;
                while (i < pathExpr.Length && pathExpr[i] != '.' && pathExpr[i] != '[')
                    i++;
                if (i == start)
                    return false;
                var propName = pathExpr.Substring(start, i - start);
                if (current.ValueKind != JsonValueKind.Object)
                    return false;
                if (!current.TryGetProperty(propName, out var next))
                    return false;
                current = next;
            }
            else if (c == '[')
            {
                var end = pathExpr.IndexOf(']', i + 1);
                if (end < 0)
                    return false;
                var inner = pathExpr.Substring(i + 1, end - i - 1).Trim();
                if (inner.Length >= 2
                    && ((inner[0] == '\'' && inner[^1] == '\'')
                        || (inner[0] == '"' && inner[^1] == '"')))
                {
                    var key = inner[1..^1];
                    if (current.ValueKind != JsonValueKind.Object)
                        return false;
                    if (!current.TryGetProperty(key, out var next))
                        return false;
                    current = next;
                }
                else if (int.TryParse(inner, out var idx))
                {
                    if (current.ValueKind != JsonValueKind.Array)
                        return false;
                    if (idx < 0 || idx >= current.GetArrayLength())
                        return false;
                    current = current[idx];
                }
                else
                {
                    return false;
                }
                i = end + 1;
            }
            else
            {
                // Unexpected char — reject instead of skipping silently.
                return false;
            }
        }
        result = current;
        return true;
    }

    // Parses an expression captured between `{{` and `}}` (already stripped
    // of the namespace prefix — i.e. just `(.path)?(\| filter)*`) into a
    // path string and a list of filter invocations. Filters are separated
    // by `|` outside of single/double quotes; arguments live inside
    // parentheses, comma-separated, with quoted-string literals preserved
    // verbatim.
    internal readonly record struct ParsedExpression(string Path, IReadOnlyList<FilterCall> Filters);

    internal readonly record struct FilterCall(string Name, IReadOnlyList<string> Args);

    internal static ParsedExpression ParseExpression(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new ParsedExpression(string.Empty, Array.Empty<FilterCall>());

        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        int paren = 0;
        foreach (var c in raw)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
            if (c == '(') { paren++; sb.Append(c); continue; }
            if (c == ')') { if (paren > 0) paren--; sb.Append(c); continue; }
            if (c == '|' && paren == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());

        var path = parts[0].Trim();
        var filters = new List<FilterCall>(parts.Count - 1);
        for (int p = 1; p < parts.Count; p++)
        {
            var token = parts[p].Trim();
            if (token.Length == 0) continue;
            var lp = token.IndexOf('(');
            string name;
            IReadOnlyList<string> args;
            if (lp < 0)
            {
                name = token;
                args = Array.Empty<string>();
            }
            else
            {
                var rp = token.LastIndexOf(')');
                if (rp <= lp)
                {
                    name = token;
                    args = Array.Empty<string>();
                }
                else
                {
                    name = token[..lp].Trim();
                    args = SplitArgs(token.Substring(lp + 1, rp - lp - 1));
                }
            }
            filters.Add(new FilterCall(name, args));
        }
        return new ParsedExpression(path, filters);
    }

    private static List<string> SplitArgs(string raw)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in raw)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
            if (c == ',') { parts.Add(Unquote(sb.ToString().Trim())); sb.Clear(); continue; }
            sb.Append(c);
        }
        var last = sb.ToString().Trim();
        if (last.Length > 0 || parts.Count > 0) parts.Add(Unquote(last));
        return parts;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"'))
            ? s[1..^1]
            : s;

    // Applies a chain of filters to a resolved value. `resolved` is null
    // when the path didn't resolve — only the `default` filter handles
    // that case; every other filter short-circuits to no-op so the caller
    // keeps the literal template text. Returns the post-filter value (or
    // null if the chain didn't recover from a failed resolve).
    internal static JsonElement? ApplyFilters(JsonElement? resolved, IReadOnlyList<FilterCall> filters)
    {
        var value = resolved;
        foreach (var f in filters)
        {
            if (string.Equals(f.Name, "default", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasValue(value))
                {
                    var arg = f.Args.Count > 0 ? f.Args[0] : string.Empty;
                    value = JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(arg)}\"").RootElement;
                }
                continue;
            }

            if (!HasValue(value))
                return null;

            value = f.Name.ToLowerInvariant() switch
            {
                "trim" => StringTransform(value!.Value, s => s.Trim()),
                "upper" => StringTransform(value!.Value, s => s.ToUpperInvariant()),
                "lower" => StringTransform(value!.Value, s => s.ToLowerInvariant()),
                "truncate" => StringTransform(value!.Value, s => Truncate(s, ParseInt(f.Args, 0, 200))),
                "json" => ToJsonString(value!.Value),
                "strip_ansi" => StringTransform(value!.Value, Services.Security.InputSanitizer.StripAnsi),
                "strip" => StringTransform(value!.Value, s => Services.Security.InputSanitizer.StripAnsiAndControl(s).Trim()),
                _ => value, // unknown filter: ignore (residual scan flags the literal as a typo)
            };
        }
        return value;
    }

    private static bool HasValue(JsonElement? el) =>
        el.HasValue && el.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
        && !(el.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(el.Value.GetString()));

    private static JsonElement StringTransform(JsonElement value, Func<string, string> fn)
    {
        // A text filter applied to something that is not text PASSES IT THROUGH. Stringifying
        // first would make `{{ steps.x.output.count | upper }}` yield the string "5" where the
        // author still has a number, and every downstream numeric comparison would then be
        // comparing text — silently, because "5" and 5 both look right in a log.
        if (value.ValueKind != JsonValueKind.String) return value;

        var s = value.GetString() ?? string.Empty;
        var transformed = fn(s);
        return JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(transformed)}\"").RootElement;
    }

    private static JsonElement ToJsonString(JsonElement value)
    {
        var raw = value.GetRawText();
        return JsonDocument.Parse($"\"{System.Text.Json.JsonEncodedText.Encode(raw)}\"").RootElement;
    }

    private static int ParseInt(IReadOnlyList<string> args, int index, int fallback) =>
        index < args.Count && int.TryParse(args[index], out var n) ? n : fallback;

    private static string Truncate(string s, int max) =>
        max <= 0 ? string.Empty : (s.Length <= max ? s : s[..max]);

    // `internal` so ConditionEvaluator produces identical string forms of
    // resolved values (matters for `== "completed"` style comparisons,
    // where a stringified `true` vs `"true"` can flip the outcome).
    internal static string Stringify(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        // Absence contributes NOTHING to a surrounding string. Writing the word "null" into
        // the middle of a command puts it on a device — the substitution's job is to carry a
        // value, and there is no value to carry.
        JsonValueKind.Null => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        // Objects/arrays fall back to raw JSON — loses formatting but at
        // least preserves the content for debugging.
        _ => el.GetRawText(),
    };
}
