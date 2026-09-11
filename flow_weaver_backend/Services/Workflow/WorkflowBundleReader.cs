using System.Text.Json;
using flow_weaver_backend.Exceptions;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// Recognition and parsing for the portable bundle format.
/// </summary>
/// <remarks>
/// Detection is by explicit marker (<c>schema_version</c> + <c>kind</c>), not by
/// shape. The old FlowWeaver detector guessed from the presence of a
/// <c>snippet_id</c> key on the first node, which is a shape several formats
/// share — and it never matched our own JSON export anyway, because that export
/// wrote <c>version: 1</c> while the detector looked for <c>schema_version</c>.
/// A format that travels between installations has to say what it is.
/// </remarks>
public static class WorkflowBundleReader
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Readable diffs matter: these files get committed to git repos and
        // reviewed in pull requests.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Forward compatibility (bundle/SPEC.md §1): members this build does
        // not know are ignored, never a parse failure.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// True when the payload announces itself as a bundle. Cheap and total —
    /// never throws, so the caller can use it to pick a path.
    /// </summary>
    public static bool LooksLikeBundle(string raw, string format)
    {
        // YAML is not a bundle carrier: the format is emitted as JSON only, and
        // parsing YAML here just to reject it would be wasted work.
        if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(format, "bundle", StringComparison.OrdinalIgnoreCase))
            return false;

        return LooksLikeBundle(raw);
    }

    /// <summary>
    /// The same question without a declared format, for a caller that receives a
    /// raw upload and no `format` parameter — the import wizard takes an
    /// optional `format_hint` which is normally empty.
    /// </summary>
    /// <remarks>
    /// One predicate, several entry points. Two independent answers to "is this
    /// a bundle" is how the wizard came to hand one to the foreign-format
    /// translators: a bundle carries top-level `nodes`/`edges` and `snippet_id`
    /// on its first node, so any detector chain that gets to run will claim it,
    /// and `dependencies` is discarded before anything looks for it.
    /// </remarks>
    public static bool LooksLikeBundle(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return LooksLikeBundle(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The kind check itself, for a document already parsed.</summary>
    public static bool LooksLikeBundle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        return root.TryGetProperty("kind", out var kind)
               && kind.ValueKind == JsonValueKind.String
               && WorkflowBundle.AcceptedKinds.Contains(kind.GetString(), StringComparer.Ordinal);
    }

    public static WorkflowBundle Parse(string raw)
    {
        WorkflowBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<WorkflowBundle>(raw, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new ValidationException(
                $"workflow bundle is not valid JSON: {ex.Message}", "bundle_parse_failed");
        }

        if (bundle is null)
            throw new ValidationException("workflow bundle is empty", "bundle_empty");

        // Refuse a version this build does not know rather than silently
        // reading a subset of it — a bundle that half-imports is worse than one
        // that is rejected with a version number the operator can act on.
        if (!WorkflowBundle.AcceptedSchemaVersions.Contains(bundle.SchemaVersion, StringComparer.Ordinal))
            throw new ValidationException(
                $"workflow bundle schema_version '{bundle.SchemaVersion}' is not supported by this "
                + $"instance (accepted: {string.Join(", ", WorkflowBundle.AcceptedSchemaVersions)}). "
                + "Upgrade FlowWeaver, or re-export the workflow from the source instance.",
                "bundle_version_unsupported");

        if (!WorkflowBundle.AcceptedKinds.Contains(bundle.Kind, StringComparer.Ordinal))
            throw new ValidationException(
                $"'{bundle.Kind}' is not a workflow bundle marker this instance reads "
                + $"(accepted: {string.Join(", ", WorkflowBundle.AcceptedKinds)}).",
                "bundle_kind_unsupported");

        if (string.IsNullOrWhiteSpace(bundle.Workflow.Name))
            throw new ValidationException("workflow.name is required", "name_required");

        if (bundle.Nodes.ValueKind != JsonValueKind.Array)
            throw new ValidationException("bundle nodes must be an array", "nodes_invalid");
        if (bundle.Edges.ValueKind != JsonValueKind.Array)
            bundle.Edges = JsonDocument.Parse("[]").RootElement.Clone();

        foreach (var w in bundle.Dependencies.Workflows)
        {
            if (w.Nodes.ValueKind != JsonValueKind.Array)
                throw new ValidationException(
                    $"dependencies.workflows['{w.Name}'].nodes must be an array", "nodes_invalid");
            if (w.Edges.ValueKind != JsonValueKind.Array)
                w.Edges = JsonDocument.Parse("[]").RootElement.Clone();
        }

        // A v3 bundle declares what it needs; a v2 bundle is read as v3 with
        // `requires` inferred from what is visible (bundle/SPEC.md §2.3). A
        // v3 bundle whose declaration names a capability outside the closed
        // vocabulary is refused: this build cannot know what it means, and
        // "probably fine" is exactly the failure mode the block exists to
        // prevent.
        if (bundle.IsV3 && bundle.Requires is not null)
        {
            var unsupported = BundleCapabilities.Unsupported(bundle.Requires.Capabilities);
            if (unsupported.Count > 0)
                throw new ValidationException(
                    $"the bundle requires capabilities this instance does not implement: "
                    + $"{string.Join(", ", unsupported)}. Implemented capabilities: "
                    + $"{string.Join(", ", BundleCapabilities.Implemented.OrderBy(c => c, StringComparer.Ordinal))}.",
                    "bundle_capability_unsupported",
                    unsupported);
        }

        // A v2 bundle carries NO triggers, whatever the file says (§2.3), and this runs
        // BEFORE `requires` is inferred: the inference reads the trigger list, so clearing
        // afterwards left a v2 import declaring a `triggers` capability for triggers it dropped.
        //
        // v2 predates the trigger section of this format. A `triggers` array in a v2 file is
        // therefore some other product's shape under a name this one now uses, and importing
        // it means creating a schedule or a webhook route the author never asked THIS instance
        // for. Reading it would be guessing, and a bundle that half-guesses a trigger is worse
        // than one that carries none: a cron that fires on the wrong instance is not a
        // cosmetic error.
        //
        // Dropped silently rather than noted, because there is nothing an operator can do
        // about it and nothing was lost — the workflow is complete and runnable, and a v3
        // re-export from here will carry whatever triggers they then create.
        if (!bundle.IsV3) bundle.Triggers = [];

        bundle.Requires ??= BundleRequirements.Compute(bundle);

        // Every snippet the nodes reference — in the main graph and in every
        // sub-workflow — must have travelled. A bundle missing one cannot be
        // completed by guessing: that guess is precisely the placeholder-
        // python behaviour this format exists to remove.
        var carried = bundle.Dependencies.Snippets.Select(s => s.Id).ToHashSet();
        var absent = new SortedSet<Guid>();
        foreach (var id in NodeReferences.Extract(bundle.Nodes).SnippetIds)
            if (!carried.Contains(id)) absent.Add(id);
        foreach (var w in bundle.Dependencies.Workflows)
            foreach (var id in NodeReferences.Extract(w.Nodes).SnippetIds)
                if (!carried.Contains(id)) absent.Add(id);
        if (absent.Count > 0)
            throw new ValidationException(
                "the bundle references snippets whose definitions it does not carry "
                + $"({string.Join(", ", absent)}). Re-export it from the source instance.",
                "bundle_incomplete",
                absent.Select(id => id.ToString()).ToList());

        // Sub-workflow graph: a cycle can never run (a workflow reaching
        // itself), so it is refused here, before anything is created.
        SubflowOrder(bundle);

        return bundle;
    }

    /// <summary>
    /// The bundle's sub-workflows in creation order: every workflow after the
    /// ones it calls, so <c>subflow_workflow_id</c> can be remapped as each is
    /// created. Throws <c>bundle_subflow_cycle</c> naming the loop.
    /// </summary>
    public static IReadOnlyList<BundleWorkflowDefinition> SubflowOrder(WorkflowBundle bundle)
    {
        var byId = bundle.Dependencies.Workflows
            .GroupBy(w => w.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var order = new List<BundleWorkflowDefinition>();
        var done = new HashSet<Guid>();
        var onPath = new List<Guid>();

        void Visit(Guid id)
        {
            if (done.Contains(id)) return;
            if (!byId.TryGetValue(id, out var def)) return;   // dangling: resolver decides
            var loopAt = onPath.IndexOf(id);
            if (loopAt >= 0)
            {
                var loop = onPath.Skip(loopAt).Append(id)
                    .Select(i => byId.TryGetValue(i, out var w) ? w.Name : i.ToString());
                throw new ValidationException(
                    $"the bundle's sub-workflows form a cycle ({string.Join(" → ", loop)}); a workflow "
                    + "that reaches itself can never run.",
                    "bundle_subflow_cycle");
            }
            onPath.Add(id);
            foreach (var child in NodeReferences.Extract(def.Nodes).SubflowWorkflowIds)
                Visit(child);
            onPath.RemoveAt(onPath.Count - 1);
            done.Add(id);
            order.Add(def);
        }

        foreach (var id in NodeReferences.Extract(bundle.Nodes).SubflowWorkflowIds) Visit(id);
        // Anything carried but not reachable from the main graph is still
        // created — the sender put it there for a reason — after the rest.
        foreach (var w in bundle.Dependencies.Workflows) Visit(w.Id);
        return order;
    }

    public static string FileName(string workflowName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in workflowName)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        var safe = sb.ToString().Trim('_');
        return (safe.Length == 0 ? "workflow" : safe) + ".bundle.json";
    }
}
