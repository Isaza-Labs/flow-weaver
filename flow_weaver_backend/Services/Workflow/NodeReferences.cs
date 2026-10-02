using System.Text.Json;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// Reads and rewrites what a workflow's nodes point at.
/// </summary>
/// <remarks>
/// <para>
/// One place owns the knowledge of WHERE a node keeps its references —
/// <c>snippet_id</c> on the node, and inside <c>config_overrides</c>:
/// <c>integration_id</c> / <c>action_id</c> (integration_action),
/// <c>mcp_server_id</c> (mcp_call), <c>credential_id</c> (ssh),
/// <c>repository_id</c> (git) and <c>subflow_workflow_id</c> (subflow). Export
/// reads them to decide what to describe; import rewrites them to point at
/// local rows. Splitting that knowledge across both directions is how they
/// drift.
/// </para>
/// <para>
/// The interchange standard (bundle/SPEC.md §4) gives each of those a portable
/// NAME key — <c>integration</c> (slug), <c>action</c> (name), <c>server</c>,
/// <c>credential</c>, <c>repository</c> — so a node is meaningful on its own.
/// Export writes the name key <em>in place of</em> the local id and never
/// emits the id; import translates the name key back to a local id and drops
/// it, so the stored graph keeps exactly one source of truth (this instance's
/// ids) and a later export re-derives the names from whatever the node points
/// at NOW, never from a stale label.
/// </para>
/// <para>
/// Why an id may not travel beside the name: node objects are hashed, and that
/// hash is what the §8 round trip compares. A GUID is meaningful only on the
/// instance that minted it, so carrying one would make the fingerprint
/// instance-specific — the same workflow exported from two instances would
/// hash differently, and a bundle that crossed to the other product could
/// never come back equal to itself. <c>snippet_id</c> and
/// <c>subflow_workflow_id</c> stay GUIDs because they are structural and are
/// always remapped against definitions the bundle carries.
/// </para>
/// </remarks>
public static class NodeReferences
{
    public const string SubflowSentinel = "subflow";

    // Portable name key → local id key, per reference kind.
    public const string IntegrationKey = "integration";
    public const string IntegrationIdKey = "integration_id";
    public const string ActionKey = "action";
    public const string ActionIdKey = "action_id";
    public const string ServerKey = "server";
    public const string ServerIdKey = "mcp_server_id";
    public const string CredentialKey = "credential";
    public const string CredentialIdKey = "credential_id";
    public const string RepositoryKey = "repository";
    public const string RepositoryIdKey = "repository_id";
    public const string SubflowIdKey = "subflow_workflow_id";

    // ssh inline-credential keys. Plain values are secrets and never travel;
    // a `${secret:…}` reference is fine — it names a secret, it is not one.
    public static readonly string[] InlineCredentialKeys =
        ["username", "password", "private_key", "key_passphrase"];

    public const string SecretRefPrefix = "${secret:";

    public sealed record Refs(
        HashSet<Guid> SnippetIds,
        HashSet<Guid> IntegrationIds,
        HashSet<Guid> ActionIds,
        HashSet<Guid> McpServerIds,
        HashSet<Guid> CredentialIds,
        HashSet<Guid> RepositoryIds,
        HashSet<Guid> SubflowWorkflowIds,
        HashSet<string> IntegrationNames,
        HashSet<(string Integration, string Action)> ActionNames,
        // The same pairing for a node that identifies its integration by a LEGACY ID
        // instead of by name. §4 accepts that spelling when the bundle's dependencies
        // carry the mapping, and without this set the action half is unreachable: the
        // pre-resolution pass is keyed by integration NAME, so such a node contributes
        // no key and its action never gets looked up.
        HashSet<(Guid Integration, string Action)> ActionNamesByIntegrationId,
        HashSet<string> ServerNames,
        HashSet<string> CredentialNames,
        HashSet<string> RepositoryNames)
    {
        public static Refs Empty() => new(
            new(), new(), new(), new(), new(), new(), new(),
            new(StringComparer.OrdinalIgnoreCase),
            new(),
            new(),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));

        public void UnionWith(Refs other)
        {
            SnippetIds.UnionWith(other.SnippetIds);
            IntegrationIds.UnionWith(other.IntegrationIds);
            ActionIds.UnionWith(other.ActionIds);
            McpServerIds.UnionWith(other.McpServerIds);
            CredentialIds.UnionWith(other.CredentialIds);
            RepositoryIds.UnionWith(other.RepositoryIds);
            SubflowWorkflowIds.UnionWith(other.SubflowWorkflowIds);
            IntegrationNames.UnionWith(other.IntegrationNames);
            ActionNames.UnionWith(other.ActionNames);
            ActionNamesByIntegrationId.UnionWith(other.ActionNamesByIntegrationId);
            ServerNames.UnionWith(other.ServerNames);
            CredentialNames.UnionWith(other.CredentialNames);
            RepositoryNames.UnionWith(other.RepositoryNames);
        }
    }

    public static Refs Extract(JsonElement nodes)
    {
        var refs = Refs.Empty();
        if (nodes.ValueKind != JsonValueKind.Array) return refs;

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;

            if (TryGuid(node, "snippet_id", out var snippetId)) refs.SnippetIds.Add(snippetId);

            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object) continue;

            if (TryGuid(overrides, IntegrationIdKey, out var integrationId)) refs.IntegrationIds.Add(integrationId);
            if (TryGuid(overrides, ActionIdKey, out var actionId)) refs.ActionIds.Add(actionId);
            if (TryGuid(overrides, ServerIdKey, out var serverId)) refs.McpServerIds.Add(serverId);
            if (TryGuid(overrides, CredentialIdKey, out var credentialId)) refs.CredentialIds.Add(credentialId);
            if (TryGuid(overrides, RepositoryIdKey, out var repositoryId)) refs.RepositoryIds.Add(repositoryId);
            if (TryGuid(overrides, SubflowIdKey, out var subflowId)) refs.SubflowWorkflowIds.Add(subflowId);

            var integration = ReadString(overrides, IntegrationKey);
            if (integration is not null) refs.IntegrationNames.Add(integration);
            var action = ReadString(overrides, ActionKey);
            if (action is not null && integration is not null) refs.ActionNames.Add((integration, action));
            // The node named its integration by id rather than by name — a v2 spelling §4
            // still accepts. The action is looked up through the bundle's id mapping instead.
            else if (action is not null && TryGuid(overrides, IntegrationIdKey, out var actionOwnerId))
                refs.ActionNamesByIntegrationId.Add((actionOwnerId, action));
            var server = ReadString(overrides, ServerKey);
            if (server is not null) refs.ServerNames.Add(server);
            var credential = ReadString(overrides, CredentialKey);
            if (credential is not null) refs.CredentialNames.Add(credential);
            var repository = ReadString(overrides, RepositoryKey);
            if (repository is not null) refs.RepositoryNames.Add(repository);
        }

        return refs;
    }

    /// <summary>
    /// True for a node that runs another workflow: <c>snippet_id: "subflow"</c>
    /// (the sentinel the engine dispatches on) or <c>type: "subflow"</c>.
    /// </summary>
    public static bool IsSubflowNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;
        if (node.TryGetProperty("snippet_id", out var sid) && sid.ValueKind == JsonValueKind.String
            && string.Equals(sid.GetString(), SubflowSentinel, StringComparison.Ordinal))
            return true;
        return node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
               && string.Equals(type.GetString(), SubflowSentinel, StringComparison.Ordinal);
    }

    /// <summary>The snippet id of every node, keyed by node id (sentinels excluded).</summary>
    public static IReadOnlyDictionary<string, Guid> SnippetIdByNode(JsonElement nodes)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (nodes.ValueKind != JsonValueKind.Array) return map;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(node, "id");
            if (id is null) continue;
            if (TryGuid(node, "snippet_id", out var snippetId)) map[id] = snippetId;
        }
        return map;
    }

    // ── export: portable name keys beside the local ids ─────────────────

    /// <summary>What each local id is called portably.</summary>
    public sealed record ExportNames(
        IReadOnlyDictionary<Guid, string> Integrations,
        IReadOnlyDictionary<Guid, string> Actions,
        IReadOnlyDictionary<Guid, string> McpServers,
        IReadOnlyDictionary<Guid, string> Credentials,
        IReadOnlyDictionary<Guid, string> Repositories);

    /// <summary>An id key the export could not translate to a portable name.</summary>
    public sealed record Unnameable(string NodeId, string Key, string Value);

    /// <inheritdoc cref="Annotate(JsonElement, ExportNames, out List{Unnameable})"/>
    public static JsonElement Annotate(JsonElement nodes, ExportNames names)
        => Annotate(nodes, names, out _);

    /// <summary>
    /// Returns <paramref name="nodes"/> with every local id key replaced, in
    /// place, by its portable name key — bundle/SPEC.md §4: an exporter MUST
    /// write the canonical key and MUST NOT emit <c>integration_id</c>,
    /// <c>action_id</c>, <c>mcp_server_id</c>, <c>credential_id</c> or
    /// <c>repository_id</c>. A name key already on the node is re-derived from
    /// the id when one is present (the id is what the node actually runs
    /// against) and kept verbatim when there is nothing to derive it from.
    /// </summary>
    /// <remarks>
    /// An id this instance can no longer name — the row is gone, so it is
    /// absent from <paramref name="names"/> and from the bundle's dependencies
    /// alike — is left as the id and reported in <paramref name="unnameable"/>.
    /// Dropping it would strip the reference silently and produce a bundle
    /// that imports clean and fails at run time; leaving it makes the
    /// receiving importer refuse loudly with
    /// <c>bundle_reference_untranslatable</c>, which is the honest outcome for
    /// a graph that is already broken on this side.
    /// </remarks>
    public static JsonElement Annotate(
        JsonElement nodes, ExportNames names, out List<Unnameable> unnameable)
    {
        var unnamed = new List<Unnameable>();
        unnameable = unnamed;
        if (nodes.ValueKind != JsonValueKind.Array) return nodes;

        return Rewrite(nodes, (writer, nodeId, overrides) =>
        {
            var hasId = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in overrides.EnumerateObject())
                if (IsIdKey(p.Name)) hasId.Add(p.Name);

            foreach (var prop in overrides.EnumerateObject())
            {
                if (NameKeyToIdKey(prop.Name) is { } idKey)
                {
                    // Re-derived from the id below; keep only when there is
                    // no id to derive it from.
                    if (!hasId.Contains(idKey)) prop.WriteTo(writer);
                    continue;
                }

                if (!IsIdKey(prop.Name))
                {
                    prop.WriteTo(writer);
                    continue;
                }

                var table = prop.Name switch
                {
                    IntegrationIdKey => names.Integrations,
                    ActionIdKey => names.Actions,
                    ServerIdKey => names.McpServers,
                    CredentialIdKey => names.Credentials,
                    _ => names.Repositories,
                };

                if (prop.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(prop.Value.GetString(), out var id)
                    && table.TryGetValue(id, out var name))
                {
                    // The id key's slot now holds the portable name; the id
                    // itself does not travel.
                    writer.WriteString(IdKeyToNameKey(prop.Name)!, name);
                    continue;
                }

                // Untranslatable: keep the id so the far side refuses rather
                // than importing a node that points at nothing.
                prop.WriteTo(writer);
                unnamed.Add(new Unnameable(
                    nodeId, prop.Name,
                    prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText()));
            }
        });
    }

    // ── import: portable name keys → local ids ──────────────────────────

    /// <summary>
    /// Everything the importer resolved locally, keyed the way the nodes
    /// wrote it. Names are matched case-insensitively (bundle/SPEC.md §5.1:
    /// exact, never fuzzy). Actions are keyed by the LOCAL integration id
    /// they belong to plus their name, since names are only unique within an
    /// integration.
    /// </summary>
    public sealed class ImportLookup
    {
        public Dictionary<string, Guid> Integrations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> McpServers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> Credentials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> Repositories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, Guid> IdMap { get; } = new();

        public static string ActionKeyFor(Guid localIntegrationId, string actionName)
            => $"{localIntegrationId:N}{actionName}";
    }

    public sealed record Untranslatable(string NodeId, string Key, string Value);

    /// <summary>
    /// Returns <paramref name="nodes"/> with every portable name key replaced
    /// by its local id key, every legacy id remapped through the lookup's
    /// <c>IdMap</c>, and the name keys dropped. When a node carries both, the
    /// name wins (bundle/SPEC.md §4). Anything that resolves to nothing is
    /// reported in <paramref name="errors"/> — node and key — rather than
    /// guessed. <c>subflow_workflow_id</c> is left for <see cref="Remap"/>,
    /// because sub-workflow ids only exist once the sub-workflows do.
    /// </summary>
    public static JsonElement Translate(
        JsonElement nodes, ImportLookup lookup, List<Untranslatable> errors)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return nodes;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    node.WriteTo(writer);
                    continue;
                }

                var nodeId = ReadString(node, "id") ?? "?";
                writer.WriteStartObject();
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("snippet_id") && TryMap(prop.Value, lookup.IdMap, out var mappedSnippet))
                    {
                        writer.WriteString("snippet_id", mappedSnippet);
                        continue;
                    }
                    if (prop.NameEquals("config_overrides") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("config_overrides");
                        TranslateOverrides(writer, nodeId, prop.Value, lookup, errors);
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void TranslateOverrides(
        Utf8JsonWriter writer, string nodeId, JsonElement overrides,
        ImportLookup lookup, List<Untranslatable> errors)
    {
        // Resolve every kind first so the write pass is a straight copy with
        // substitutions, and so `action` can see the integration it belongs to.
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);

        Guid? localIntegration = ResolveKind(
            overrides, nodeId, IntegrationKey, IntegrationIdKey, lookup.Integrations, lookup.IdMap, errors);
        if (localIntegration is { } li) resolved[IntegrationIdKey] = li;

        var actionName = ReadString(overrides, ActionKey);
        if (actionName is not null)
        {
            if (localIntegration is { } owner
                && lookup.Actions.TryGetValue(ImportLookup.ActionKeyFor(owner, actionName), out var localAction))
                resolved[ActionIdKey] = localAction;
            else
                errors.Add(new Untranslatable(nodeId, ActionKey, actionName));
        }
        else if (TryGuid(overrides, ActionIdKey, out var legacyAction))
        {
            if (lookup.IdMap.TryGetValue(legacyAction, out var localAction))
                resolved[ActionIdKey] = localAction;
            else
                errors.Add(new Untranslatable(nodeId, ActionIdKey, legacyAction.ToString()));
        }

        if (ResolveKind(overrides, nodeId, ServerKey, ServerIdKey, lookup.McpServers, lookup.IdMap, errors) is { } server)
            resolved[ServerIdKey] = server;
        if (ResolveKind(overrides, nodeId, CredentialKey, CredentialIdKey, lookup.Credentials, lookup.IdMap, errors) is { } cred)
            resolved[CredentialIdKey] = cred;
        if (ResolveKind(overrides, nodeId, RepositoryKey, RepositoryIdKey, lookup.Repositories, lookup.IdMap, errors) is { } repo)
            resolved[RepositoryIdKey] = repo;

        var written = new HashSet<string>(StringComparer.Ordinal);
        writer.WriteStartObject();
        foreach (var prop in overrides.EnumerateObject())
        {
            var idKey = NameKeyToIdKey(prop.Name) ?? (IsIdKey(prop.Name) ? prop.Name : null);
            if (idKey is not null)
            {
                // First of (name key | id key) for this kind decides the
                // position; the other is dropped.
                if (written.Add(idKey))
                {
                    if (resolved.TryGetValue(idKey, out var local))
                        writer.WriteString(idKey, local.ToString());
                    else if (prop.Name == idKey)
                        prop.WriteTo(writer);   // unresolved id, already reported
                    else
                        prop.WriteTo(writer);   // unresolved name, already reported
                }
                continue;
            }
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static Guid? ResolveKind(
        JsonElement overrides, string nodeId, string nameKey, string idKey,
        IReadOnlyDictionary<string, Guid> byName, IReadOnlyDictionary<Guid, Guid> idMap,
        List<Untranslatable> errors)
    {
        var name = ReadString(overrides, nameKey);
        if (name is not null)
        {
            if (byName.TryGetValue(name, out var local)) return local;
            errors.Add(new Untranslatable(nodeId, nameKey, name));
            return null;
        }
        if (TryGuid(overrides, idKey, out var legacy))
        {
            if (idMap.TryGetValue(legacy, out var local)) return local;
            errors.Add(new Untranslatable(nodeId, idKey, legacy.ToString()));
        }
        return null;
    }

    // ── id remap (same-instance and post-translation) ───────────────────

    /// <summary>
    /// Returns <paramref name="nodes"/> with every referenced GUID replaced via
    /// <paramref name="idMap"/>. Ids absent from the map are left untouched:
    /// the sentinel <c>__start__</c> / <c>__end__</c> / <c>subflow</c> nodes
    /// carry non-GUID snippet ids, and anything genuinely unresolved was
    /// already rejected by the resolver before this runs.
    /// </summary>
    /// <summary>
    /// Renames an imported graph's start and end nodes to the ids this product
    /// creates for every new workflow, rewriting the edges that name them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A workflow created here is born with a start and an end whose NODE id is
    /// the sentinel itself. An imported graph carries its own, and the exporting
    /// product is free to call them anything — one writes
    /// <c>{"id":"start","snippet_id":"__start__"}</c>, which is valid: the engine
    /// has always identified a sentinel by <c>snippet_id</c>.
    /// </para>
    /// <para>
    /// Valid and still wrong for an import, because the receiving workflow is not
    /// empty when the graph lands in it — it already has the two the product made.
    /// An import that brings its own leaves four, and the two it brought are the
    /// only ones the edges reach. So the ends are adopted rather than created:
    /// the incoming sentinel takes the local id, and every edge naming it follows.
    /// </para>
    /// <para>
    /// This changes the node ids, and node objects are hashed — so a bundle
    /// re-exported after an import no longer fingerprints identically to the one
    /// that produced it, in the same way canvas coordinates already do not. The
    /// alternative is that every producer emits the canonical id, which would
    /// remove the need for this entirely; until then, converging on import is the
    /// half this product controls.
    /// </para>
    /// <para>
    /// Only the FIRST node per marker is adopted, and only when nothing else
    /// already holds that id. Two start nodes is a malformed graph, and merging
    /// them onto one id would hide it instead of letting validation refuse it.
    /// </para>
    /// </remarks>
    public static (JsonElement Nodes, JsonElement Edges) AdoptLocalSentinels(
        JsonElement nodes, JsonElement edges)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return (nodes, edges);

        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in nodes.EnumerateArray())
            if (n.ValueKind == JsonValueKind.Object
                && n.TryGetProperty("id", out var idProp)
                && idProp.ValueKind == JsonValueKind.String
                && idProp.GetString() is { } id)
                taken.Add(id);

        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var marker in new[] { StartSentinel, EndSentinel })
        {
            if (taken.Contains(marker)) continue;   // already canonical, or occupied
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                if (!n.TryGetProperty("snippet_id", out var snip)
                    || snip.ValueKind != JsonValueKind.String
                    || !string.Equals(snip.GetString(), marker, StringComparison.Ordinal)) continue;
                if (!n.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.String
                    || idProp.GetString() is not { } id) continue;
                if (rename.ContainsKey(id)) break;
                rename[id] = marker;
                break;                              // first only
            }
        }

        if (rename.Count == 0) return (nodes, edges);

        return (RewriteIds(nodes, "id", rename), RewriteIds(edges, "source", rename, "target"));
    }

    public const string StartSentinel = "__start__";
    public const string EndSentinel = "__end__";

    // Rewrites the named string properties of every object in an array.
    private static JsonElement RewriteIds(
        JsonElement array, string first, IReadOnlyDictionary<string, string> rename, string? second = null)
    {
        if (array.ValueKind != JsonValueKind.Array) return array;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { item.WriteTo(writer); continue; }
                writer.WriteStartObject();
                foreach (var prop in item.EnumerateObject())
                {
                    var renameable = prop.NameEquals(first) || (second is not null && prop.NameEquals(second));
                    if (renameable
                        && prop.Value.ValueKind == JsonValueKind.String
                        && prop.Value.GetString() is { } v
                        && rename.TryGetValue(v, out var replacement))
                    {
                        writer.WriteString(prop.Name, replacement);
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public static JsonElement Remap(JsonElement nodes, IReadOnlyDictionary<Guid, Guid> idMap)
    {
        if (nodes.ValueKind != JsonValueKind.Array || idMap.Count == 0) return nodes;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
                WriteNode(writer, node, idMap);
            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteNode(
        Utf8JsonWriter writer, JsonElement node, IReadOnlyDictionary<Guid, Guid> idMap)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            node.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.NameEquals("snippet_id") && TryMap(prop.Value, idMap, out var mappedSnippet))
            {
                writer.WriteString("snippet_id", mappedSnippet);
                continue;
            }

            if (prop.NameEquals("config_overrides") && prop.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("config_overrides");
                writer.WriteStartObject();
                foreach (var o in prop.Value.EnumerateObject())
                {
                    if ((IsIdKey(o.Name) || o.NameEquals(SubflowIdKey)) && TryMap(o.Value, idMap, out var mapped))
                    {
                        writer.WriteString(o.Name, mapped);
                        continue;
                    }
                    o.WriteTo(writer);
                }
                writer.WriteEndObject();
                continue;
            }

            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    // ── inline ssh credentials ──────────────────────────────────────────

    /// <summary>
    /// (node, key) for every plain-text inline credential on an ssh node. A
    /// bundle is passed around in chat and committed to git; a password in
    /// it is a leak, so export strips these and import refuses them
    /// (snippets/SPEC.md `ssh`). A <c>${secret:…}</c> reference is allowed.
    /// </summary>
    public static IReadOnlyList<(string NodeId, string Key)> FindPlainInlineCredentials(
        JsonElement nodes, IReadOnlySet<Guid> sshSnippetIds)
    {
        var found = new List<(string, string)>();
        if (nodes.ValueKind != JsonValueKind.Array) return found;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!IsSshNode(node, sshSnippetIds)) continue;
            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object) continue;
            var nodeId = ReadString(node, "id") ?? "?";
            foreach (var key in InlineCredentialKeys)
                if (IsPlainValue(overrides, key)) found.Add((nodeId, key));
        }
        return found;
    }

    /// <summary>Returns the nodes with plain-text inline credentials removed from ssh nodes.</summary>
    public static JsonElement StripPlainInlineCredentials(
        JsonElement nodes, IReadOnlySet<Guid> sshSnippetIds, out List<string> strippedNodes)
    {
        var stripped = new List<string>();
        strippedNodes = stripped;
        if (nodes.ValueKind != JsonValueKind.Array) return nodes;
        if (FindPlainInlineCredentials(nodes, sshSnippetIds).Count == 0) return nodes;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (!IsSshNode(node, sshSnippetIds))
                {
                    node.WriteTo(writer);
                    continue;
                }
                var nodeId = ReadString(node, "id") ?? "?";
                var touched = false;
                writer.WriteStartObject();
                foreach (var prop in node.EnumerateObject())
                {
                    if (!prop.NameEquals("config_overrides") || prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        prop.WriteTo(writer);
                        continue;
                    }
                    writer.WritePropertyName("config_overrides");
                    writer.WriteStartObject();
                    foreach (var o in prop.Value.EnumerateObject())
                    {
                        if (InlineCredentialKeys.Contains(o.Name) && IsPlainValue(prop.Value, o.Name))
                        {
                            touched = true;
                            continue;
                        }
                        o.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                if (touched) stripped.Add(nodeId);
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static bool IsSshNode(JsonElement node, IReadOnlySet<Guid> sshSnippetIds)
        => node.ValueKind == JsonValueKind.Object
           && TryGuid(node, "snippet_id", out var sid) && sshSnippetIds.Contains(sid);

    private static bool IsPlainValue(JsonElement overrides, string key)
    {
        if (!overrides.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (v.ValueKind != JsonValueKind.String) return true;
        var s = v.GetString() ?? string.Empty;
        return s.Length > 0 && !s.TrimStart().StartsWith(SecretRefPrefix, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static JsonElement Rewrite(
        JsonElement nodes, Action<Utf8JsonWriter, string, JsonElement> writeOverrides)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    node.WriteTo(writer);
                    continue;
                }
                var nodeId = ReadString(node, "id") ?? "?";
                writer.WriteStartObject();
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("config_overrides") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("config_overrides");
                        writer.WriteStartObject();
                        writeOverrides(writer, nodeId, prop.Value);
                        writer.WriteEndObject();
                        continue;
                    }
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static bool IsIdKey(string name) => name is
        IntegrationIdKey or ActionIdKey or ServerIdKey or CredentialIdKey or RepositoryIdKey;

    private static string? IdKeyToNameKey(string name) => name switch
    {
        IntegrationIdKey => IntegrationKey,
        ActionIdKey => ActionKey,
        ServerIdKey => ServerKey,
        CredentialIdKey => CredentialKey,
        RepositoryIdKey => RepositoryKey,
        _ => null,
    };

    private static string? NameKeyToIdKey(string name) => name switch
    {
        IntegrationKey => IntegrationIdKey,
        ActionKey => ActionIdKey,
        ServerKey => ServerIdKey,
        CredentialKey => CredentialIdKey,
        RepositoryKey => RepositoryIdKey,
        _ => null,
    };

    private static bool TryMap(
        JsonElement value, IReadOnlyDictionary<Guid, Guid> idMap, out string mapped)
    {
        mapped = string.Empty;
        if (value.ValueKind != JsonValueKind.String) return false;
        if (!Guid.TryParse(value.GetString(), out var id)) return false;
        if (!idMap.TryGetValue(id, out var local)) return false;
        mapped = local.ToString();
        return true;
    }

    private static bool TryGuid(JsonElement obj, string property, out Guid value)
    {
        value = Guid.Empty;
        return obj.TryGetProperty(property, out var el)
               && el.ValueKind == JsonValueKind.String
               && Guid.TryParse(el.GetString(), out value);
    }

    private static string? ReadString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()
            : null;
}
