using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Worker;
using Models = flow_weaver_backend.Models;

namespace flow_weaver_backend.Services.Workflow;

public interface IWorkflowBundleService
{
    /// <summary>Builds the portable (v3) bundle for a workflow.</summary>
    Task<WorkflowBundle> BuildAsync(Guid workflowId, CancellationToken ct);

    /// <summary>
    /// Resolves a bundle's dependencies against THIS instance: every source
    /// id paired with the local id that replaces it, the node graphs with
    /// their portable name keys translated to local ids, the snippets that
    /// would have to be created, the sub-workflows in creation order and the
    /// triggers to create. Throws <see cref="ValidationException"/> naming
    /// everything that cannot be resolved.
    /// </summary>
    Task<BundleResolution> ResolveAsync(WorkflowBundle bundle, CancellationToken ct);

    /// <summary>
    /// An existing active workflow with exactly this name AND identical
    /// canonical nodes+edges (bundle/SPEC.md §5.4), or null. What lets a
    /// bundle be imported twice without cloning its sub-workflows.
    /// </summary>
    Task<Guid?> FindIdenticalWorkflowAsync(
        string name, JsonElement nodes, JsonElement edges, CancellationToken ct);
}

/// <summary>
/// What a bundle's dependencies map to locally.
/// </summary>
/// <param name="IdMap">Every source GUID paired with the local GUID that replaces it.</param>
/// <param name="SnippetsToCreate">Snippets that do not exist here yet, built from their embedded definitions.</param>
/// <param name="Nodes">The main graph with names translated to local ids. <c>subflow_workflow_id</c> still holds source ids — the importer remaps those as it creates the sub-workflows.</param>
/// <param name="Subflows">Sub-workflows in creation order (callees first), nodes translated the same way.</param>
/// <param name="Triggers">Triggers to create on the imported workflow: disabled, no targets, signed.</param>
/// <param name="Notes">Every degradation and everything the operator still has to configure.</param>
public sealed record BundleResolution(
    IReadOnlyDictionary<Guid, Guid> IdMap,
    IReadOnlyList<Models.Snippet> SnippetsToCreate,
    JsonElement Nodes,
    IReadOnlyList<ResolvedSubflow> Subflows,
    IReadOnlyList<CreateWorkflowTrigger> Triggers,
    IReadOnlyList<string> Notes);

public sealed record ResolvedSubflow(BundleWorkflowDefinition Definition, JsonElement Nodes);

/// <summary>
/// Export and dependency resolution for the portable bundle format.
/// </summary>
/// <remarks>
/// Deliberately separate from the <c>Services/Import</c> pipeline. That pipeline
/// exists to translate FOREIGN definitions (n8n, Itential) and is fuzzy by
/// necessity — it scores name similarity and offers to stub what it cannot
/// place. Applying it to our own export is what turned "import the workflow my
/// colleague sent me" into "generate placeholder python snippets": the export
/// carried only GUIDs, the fuzzy matcher scored those GUID strings against
/// integration names, matched nothing, and fell through to stubbing.
///
/// A bundle from another FlowWeaver — or from Nashira, which adopted the same
/// format — is not a foreign format. It resolves deterministically or it fails
/// with a list — never a guess.
/// </remarks>
public sealed class WorkflowBundleService(
    IWorkflowRepository workflows,
    ISnippetRepository snippets,
    IIntegrationRepository integrations,
    IIntegrationActionRepository actions,
    IMcpServerRepository mcpServers,
    ICredentialRepository credentials,
    IGitRepositoryRepository repositories,
    IWorkflowTriggerRepository triggers,
    IEnumerable<ISnippetHandler> handlers,
    ICurrentUser caller,
    ILogger<WorkflowBundleService> logger) : IWorkflowBundleService
{
    private const string SshType = "ssh";

    // ── export ──────────────────────────────────────────────────────────

    public async Task<WorkflowBundle> BuildAsync(Guid workflowId, CancellationToken ct)
    {
        var wf = await workflows.GetByIdAsync(workflowId, tracking: false, ct: ct)
            ?? throw new NotFoundException("workflow", workflowId);

        // Every workflow reachable through subflow nodes, transitively, once
        // each (bundle/SPEC.md §5.4). A cycle can never run, so it is refused
        // here rather than exported for the far side to discover.
        var subflows = new List<Models.Workflow>();
        var seen = new HashSet<Guid> { wf.WorkflowId };
        var path = new List<Guid> { wf.WorkflowId };
        await CollectSubflowsAsync(wf, subflows, seen, path, ct);

        var refs = NodeReferences.Extract(wf.Nodes);
        foreach (var sub in subflows) refs.UnionWith(NodeReferences.Extract(sub.Nodes));

        var snippetRows = await snippets.ListByIdsAsync(refs.SnippetIds, ct: ct);
        var integrationRows = await integrations.ListByIdsAsync(refs.IntegrationIds, ct: ct);
        var serverRows = await mcpServers.ListByIdsAsync(refs.McpServerIds, ct: ct);
        var credentialRows = await credentials.ListByIdsAsync(refs.CredentialIds, ct: ct);
        var repositoryRows = await repositories.ListByIdsAsync(refs.RepositoryIds, ct: ct);

        var actionNames = new Dictionary<Guid, string>();
        var bundleIntegrations = new List<BundleIntegration>();
        foreach (var integration in integrationRows)
        {
            // Only the actions this workflow actually uses. Shipping the whole
            // catalogue would bloat the file and leak the shape of systems the
            // recipient has no business knowing about.
            var owned = (await actions.ListActiveByIntegrationAsync(integration.IntegrationId, ct))
                .Where(a => refs.ActionIds.Contains(a.IntegrationActionId))
                .Select(a => new BundleAction
                {
                    Id = a.IntegrationActionId,
                    Name = a.Name,
                    Method = a.Method,
                    Path = a.Path,
                })
                .ToList();
            foreach (var a in owned) actionNames[a.Id] = a.Name;

            bundleIntegrations.Add(new BundleIntegration
            {
                Id = integration.IntegrationId,
                Slug = integration.Slug,
                Name = integration.Name,
                Type = integration.Type,
                BaseUrl = integration.BaseURL,
                Actions = owned,
                // AuthConfig / Headers are deliberately absent — see the note
                // on BundleIntegration. Credentials do not travel.
            });
        }

        // The portable name of every local id (bundle/SPEC.md §4). An
        // integration is named by its slug — the identity that survives a
        // rename — falling back to its name for a row that predates slugs.
        var names = new NodeReferences.ExportNames(
            integrationRows.ToDictionary(i => i.IntegrationId, i => i.Slug ?? i.Name),
            actionNames,
            serverRows.ToDictionary(s => s.McpServerId, s => s.Name),
            credentialRows.ToDictionary(c => c.CredentialId, c => c.Name),
            repositoryRows.ToDictionary(r => r.GitRepositoryId, r => r.Name));

        var sshSnippetIds = snippetRows
            .Where(s => string.Equals(s.Type, SshType, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SnippetId)
            .ToHashSet();

        var triggerRows = await triggers.ListByWorkflowAsync(wf.WorkflowId, limit: 1000, offset: 0, ct);

        var bundle = new WorkflowBundle
        {
            ExportedAt = DateTime.UtcNow,
            ExportedBy = new BundleExportedBy { Product = "flow-weaver", Version = BuildVersion() },
            Workflow = new BundleWorkflow
            {
                Name = wf.Name,
                Description = wf.Description,
                Environment = wf.Environment,
                InputSchema = wf.InputSchema,
                Metadata = wf.Metadata,
            },
            Nodes = PortableNodes(wf, names, sshSnippetIds),
            Edges = wf.Edges,
            Dependencies = new BundleDependencies
            {
                Snippets = snippetRows.Select(ToBundleSnippet).ToList(),
                Integrations = bundleIntegrations,
                McpServers = serverRows.Select(s => new BundleMcpServer
                {
                    Id = s.McpServerId, Name = s.Name, Transport = s.Transport,
                    // Url, AuthType, AuthConfigEncrypted, Headers: never.
                }).ToList(),
                Credentials = credentialRows.Select(c => new BundleCredential
                {
                    Id = c.CredentialId, Name = c.Name, Type = c.Type,
                    AuthMethod = c.AuthMethod, Username = c.Username,
                    // Encrypted* columns: never.
                }).ToList(),
                Repositories = repositoryRows.Select(r => new BundleRepository
                {
                    Id = r.GitRepositoryId, Name = r.Name, RemoteUrl = r.Url,
                    // AuthCredentialId: never — the far side binds its own.
                }).ToList(),
                Workflows = subflows.Select(s => new BundleWorkflowDefinition
                {
                    Id = s.WorkflowId,
                    Name = s.Name,
                    Description = s.Description,
                    InputSchema = s.InputSchema,
                    Metadata = s.Metadata,
                    Nodes = PortableNodes(s, names, sshSnippetIds),
                    Edges = s.Edges,
                }).ToList(),
            },
            Triggers = triggerRows.Select(ToBundleTrigger).ToList(),
        };
        bundle.Requires = BundleRequirements.Compute(bundle);

        logger.LogInformation(
            "workflow.bundle.built workflow_id={WorkflowId} snippets={Snippets} integrations={Integrations} "
            + "mcp_servers={McpServers} credentials={Credentials} repositories={Repositories} "
            + "subflows={Subflows} triggers={Triggers} capabilities={Capabilities}",
            workflowId, snippetRows.Count, bundleIntegrations.Count, serverRows.Count,
            credentialRows.Count, repositoryRows.Count, subflows.Count, triggerRows.Count,
            string.Join(",", bundle.Requires.Capabilities));

        return bundle;
    }

    private async Task CollectSubflowsAsync(
        Models.Workflow parent, List<Models.Workflow> collected,
        HashSet<Guid> seen, List<Guid> path, CancellationToken ct)
    {
        foreach (var childId in NodeReferences.Extract(parent.Nodes).SubflowWorkflowIds)
        {
            if (path.Contains(childId))
                throw new ValidationException(
                    $"workflow '{parent.Name}' reaches itself through its sub-workflows "
                    + $"({string.Join(" → ", path.Select(p => p.ToString()))} → {childId}); a cycle can never run.",
                    "bundle_subflow_cycle");
            if (!seen.Add(childId)) continue;

            var child = await workflows.GetByIdAsync(childId, tracking: false, ct: ct)
                ?? throw new ValidationException(
                    $"workflow '{parent.Name}' calls sub-workflow {childId}, which does not exist or is "
                    + "inactive on this instance. Fix the subflow node before exporting.",
                    "bundle_incomplete",
                    new[] { childId.ToString() });

            collected.Add(child);
            path.Add(childId);
            await CollectSubflowsAsync(child, collected, seen, path, ct);
            path.RemoveAt(path.Count - 1);
        }
    }

    // Nodes as they travel: every local id REPLACED by its portable name
    // (bundle/SPEC.md §4 — a GUID is meaningful only here, so it must not go
    // on the wire), and plain-text inline ssh credentials removed. The latter
    // is the one place the export is not verbatim — a password in a file that
    // gets pasted into chat and committed to git is a leak, not a config
    // value.
    private JsonElement PortableNodes(
        Models.Workflow wf, NodeReferences.ExportNames names, IReadOnlySet<Guid> sshSnippetIds)
    {
        var nodes = NodeReferences.StripPlainInlineCredentials(wf.Nodes, sshSnippetIds, out var stripped);
        if (stripped.Count > 0)
            logger.LogWarning(
                "workflow.bundle.inline_credentials_stripped workflow_id={WorkflowId} nodes={Nodes}",
                wf.WorkflowId, string.Join(",", stripped));
        var portable = NodeReferences.Annotate(nodes, names, out var unnameable);
        if (unnameable.Count > 0)
            // The referenced row is gone on THIS instance, so there is no
            // portable name to write. The id stays put — the far side then
            // refuses with bundle_reference_untranslatable instead of
            // importing a node whose reference was silently dropped.
            logger.LogWarning(
                "workflow.bundle.reference_unnameable workflow_id={WorkflowId} refs={Refs}",
                wf.WorkflowId,
                string.Join(",", unnameable.Select(u => $"{u.NodeId}.{u.Key}={u.Value}")));
        return portable;
    }

    private static string? BuildVersion()
    {
        var asm = typeof(WorkflowBundleService).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString();
    }

    // ── import ──────────────────────────────────────────────────────────

    public async Task<BundleResolution> ResolveAsync(WorkflowBundle bundle, CancellationToken ct)
    {
        var lookup = new NodeReferences.ImportLookup();
        var toCreate = new List<Models.Snippet>();
        var notes = new List<string>();
        // Two lists, because they are two different faults with two different fixes.
        // `missing`: this instance lacks something the bundle correctly declared — create it
        // here and import again. `incomplete`: the bundle failed to carry its own graph, so
        // nothing done here fixes it and the refusal names the SENDER instead (§5.4).
        var missing = new List<string>();
        var incomplete = new List<string>();
        var requires = bundle.Requires ?? BundleRequirements.Compute(bundle);

        // ── capabilities ─────────────────────────────────────────────
        // The reader already refuses a v3 bundle whose DECLARED block names one
        // of these. This catches the other two doors into the same failure: a
        // v2 bundle, whose requires are inferred (§2.3), and a bundle handed
        // straight to the resolver. A non-degradable capability this instance
        // does not implement is a refusal, never a note — a note describes a
        // degradation, and there is nothing here to degrade to.
        var unsupported = BundleCapabilities.Unsupported(requires.Capabilities);
        if (unsupported.Count > 0)
        {
            logger.LogWarning(
                "workflow.bundle.capability_unsupported capabilities={Capabilities} workflow={Workflow}",
                string.Join(",", unsupported), bundle.Workflow.Name);
            throw new ValidationException(
                "the bundle requires capabilities this instance does not implement: "
                + $"{string.Join(", ", unsupported)}. Implemented capabilities: "
                + $"{string.Join(", ", BundleCapabilities.Implemented.OrderBy(c => c, StringComparer.Ordinal))}.",
                "bundle_capability_unsupported",
                unsupported);
        }

        // ── snippet types ───────────────────────────────────────────────
        // A type with no handler here cannot run, and storing it would defer
        // the discovery to a device (bundle/SPEC.md §2.1).
        var known = handlers.Select(h => h.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredTypes = requires.SnippetTypes
            .Concat(bundle.Dependencies.Snippets.Select(s => s.Type))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var type in declaredTypes)
        {
            if (known.Contains(type)) continue;
            var users = bundle.Dependencies.Snippets
                .Where(s => string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
                .Select(s => $"'{s.Name}'");
            missing.Add(
                $"snippet type '{type}' has no handler on this instance"
                + (users.Any() ? $" (used by {string.Join(", ", users)})" : string.Empty));
        }

        // ── integrations ────────────────────────────────────────────────
        // Resolved by slug first, then by name. An unresolved integration is
        // fatal: this instance would need credentials for a system it does not
        // know, and fabricating one would produce a workflow that looks wired
        // and fails at run time.
        foreach (var wanted in bundle.Dependencies.Integrations)
        {
            var (local, matchedBySlug) = await ResolveIntegrationAsync(wanted.Slug, wanted.Name, ct);
            if (local is null)
            {
                missing.Add(
                    $"integration '{wanted.Name}' (slug '{wanted.Slug ?? "—"}', type '{wanted.Type}')"
                    + (string.IsNullOrWhiteSpace(wanted.BaseUrl) ? "" : $" — source base_url {wanted.BaseUrl}"));
                continue;
            }

            lookup.IdMap[wanted.Id] = local.IntegrationId;
            if (!string.IsNullOrWhiteSpace(wanted.Slug)) lookup.Integrations[wanted.Slug] = local.IntegrationId;
            lookup.Integrations[wanted.Name] = local.IntegrationId;
            // 5.3: "An integration found BY SLUG under a different local NAME is
            // usable — the node references the slug — but the importer notes the
            // name mismatch." Noting a slug difference instead would be silent
            // in exactly the case the spec asks about, and noisy in the one it
            // does not.
            if (matchedBySlug)
            {
                if (!string.Equals(local.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
                    notes.Add(
                        $"integration slug '{wanted.Slug}' is named '{local.Name}' here and "
                        + $"'{wanted.Name}' on the source instance; the node references the slug, so it "
                        + "is bound — confirm it is the same system.");
            }
            else
            {
                notes.Add(
                    $"integration '{wanted.Name}' matched by name to local '{local.Name}' "
                    + $"(slug '{local.Slug}'), not by slug — confirm it is the same system.");
            }

            // ── that integration's actions ──────────────────────────────
            var byName = await LocalActionsByNameAsync(local.IntegrationId, ct);
            foreach (var action in wanted.Actions)
            {
                if (byName.TryGetValue(action.Name, out var localAction))
                {
                    lookup.IdMap[action.Id] = localAction;
                    continue;
                }

                // The integration exists but this operation does not — usually
                // the local spec is older. Naming the action is what lets the
                // operator fix it by re-uploading the spec.
                missing.Add(
                    $"action '{action.Name}' on integration '{local.Name}'"
                    + (string.IsNullOrWhiteSpace(action.Method) ? "" : $" ({action.Method} {action.Path})"));
            }
        }

        // ── identity-only dependencies: MCP servers, credentials, repositories ──
        foreach (var wanted in bundle.Dependencies.McpServers)
        {
            var local = await mcpServers.FindActiveByNameAsync(wanted.Name, ct);
            if (local is null)
            {
                missing.Add($"MCP server '{wanted.Name}'"
                    + (string.IsNullOrWhiteSpace(wanted.Transport) ? "" : $" (transport {wanted.Transport})"));
                continue;
            }
            lookup.IdMap[wanted.Id] = local.McpServerId;
            lookup.McpServers[wanted.Name] = local.McpServerId;
        }
        foreach (var wanted in bundle.Dependencies.Credentials)
        {
            var local = await credentials.FindActiveByNameAsync(wanted.Name, ct);
            if (local is null)
            {
                missing.Add($"credential '{wanted.Name}'"
                    + (string.IsNullOrWhiteSpace(wanted.Type) ? "" : $" (type {wanted.Type}")
                    + (string.IsNullOrWhiteSpace(wanted.AuthMethod) ? (string.IsNullOrWhiteSpace(wanted.Type) ? "" : ")") : $", {wanted.AuthMethod})"));
                continue;
            }
            lookup.IdMap[wanted.Id] = local.CredentialId;
            lookup.Credentials[wanted.Name] = local.CredentialId;
        }
        foreach (var wanted in bundle.Dependencies.Repositories)
        {
            var local = await repositories.FindActiveByNameAsync(wanted.Name, ct);
            if (local is null)
            {
                missing.Add($"git repository '{wanted.Name}'"
                    + (string.IsNullOrWhiteSpace(wanted.RemoteUrl) ? "" : $" — source remote_url {wanted.RemoteUrl}"));
                continue;
            }
            lookup.IdMap[wanted.Id] = local.GitRepositoryId;
            lookup.Repositories[wanted.Name] = local.GitRepositoryId;
        }

        // ── portable name keys the dependencies table did not cover ─────
        // A node may name its integration / server / credential / repository
        // directly (bundle/SPEC.md §4). Whatever the table did not already
        // resolve is looked up here by the same rule: slug, then exact name.
        // What still resolves to nothing is reported per node by Translate.
        var refs = NodeReferences.Extract(bundle.Nodes);
        foreach (var w in bundle.Dependencies.Workflows) refs.UnionWith(NodeReferences.Extract(w.Nodes));

        // Names the bundle DECLARED as dependencies. One of those that did not resolve is
        // already in `missing` with the richer description the declaration carries; adding it
        // again here would name the same fault twice in one refusal.
        var declaredIntegrations = bundle.Dependencies.Integrations
            .SelectMany(i => new[] { i.Name, i.Slug })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase!)!;

        foreach (var name in refs.IntegrationNames)
        {
            if (lookup.Integrations.ContainsKey(name)) continue;
            var (local, _) = await ResolveIntegrationAsync(name, name, ct);
            if (local is not null) { lookup.Integrations[name] = local.IntegrationId; continue; }

            // Nothing to resolve it AGAINST, not merely nothing it resolves TO. §5.3 codes
            // this `bundle_dependencies_missing`, and the distinction is the instruction it
            // gives an operator: "install this integration here, with its own credentials"
            // rather than "fix this node". Left to the translation pass it came out as
            // `bundle_reference_untranslatable`, which sends them to edit a node that is
            // correct.
            if (!declaredIntegrations.Contains(name))
                missing.Add($"integration '{name}' (referenced by a node, not carried by the bundle)");
        }
        foreach (var (integrationName, actionName) in refs.ActionNames)
        {
            if (!lookup.Integrations.TryGetValue(integrationName, out var localIntegration)) continue;
            var key = NodeReferences.ImportLookup.ActionKeyFor(localIntegration, actionName);
            if (lookup.Actions.ContainsKey(key)) continue;
            var byName = await LocalActionsByNameAsync(localIntegration, ct);
            if (byName.TryGetValue(actionName, out var localAction)) lookup.Actions[key] = localAction;
        }
        // Actions on an integration the node named by LEGACY ID. The bundle's dependency
        // mapping is what makes that spelling resolvable, so this runs after it is built.
        foreach (var (bundleIntegrationId, actionName) in refs.ActionNamesByIntegrationId)
        {
            if (!lookup.IdMap.TryGetValue(bundleIntegrationId, out var localIntegration)) continue;
            var key = NodeReferences.ImportLookup.ActionKeyFor(localIntegration, actionName);
            if (lookup.Actions.ContainsKey(key)) continue;
            var byName = await LocalActionsByNameAsync(localIntegration, ct);
            if (byName.TryGetValue(actionName, out var localAction)) lookup.Actions[key] = localAction;
        }

        foreach (var name in refs.ServerNames)
        {
            if (lookup.McpServers.ContainsKey(name)) continue;
            var local = await mcpServers.FindActiveByNameAsync(name, ct);
            if (local is not null) lookup.McpServers[name] = local.McpServerId;
        }
        foreach (var name in refs.CredentialNames)
        {
            if (lookup.Credentials.ContainsKey(name)) continue;
            var local = await credentials.FindActiveByNameAsync(name, ct);
            if (local is not null) lookup.Credentials[name] = local.CredentialId;
        }
        foreach (var name in refs.RepositoryNames)
        {
            if (lookup.Repositories.ContainsKey(name)) continue;
            var local = await repositories.FindActiveByNameAsync(name, ct);
            if (local is not null) lookup.Repositories[name] = local.GitRepositoryId;
        }

        // ── snippets ────────────────────────────────────────────────────
        // Reused when this instance already has them, otherwise recreated from
        // the definition the bundle carries. That is NOT the old stubbing
        // behaviour: a stub was an invented placeholder, this is the exact
        // snippet the sender exported.
        var takenSlugs = await snippets.ListTakenSlugsAsync(ct);
        foreach (var wanted in bundle.Dependencies.Snippets)
        {
            if (!known.Contains(wanted.Type)) continue;   // already reported

            var (local, matchedBySlug, typeMismatch) = await ResolveSnippetAsync(wanted, ct);
            if (typeMismatch is not null)
                notes.Add(
                    $"a local snippet named '{typeMismatch.Name}' exists but is a "
                    + $"'{typeMismatch.Type}' snippet, not a '{wanted.Type}' one; it was NOT reused. "
                    + $"'{wanted.Name}' was created from the definition the bundle carries.");
            if (local is not null)
            {
                lookup.IdMap[wanted.Id] = local.SnippetId;
                if (matchedBySlug)
                {
                    if (!string.Equals(local.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
                        notes.Add(
                            $"snippet slug '{wanted.Slug}' is named '{local.Name}' here and "
                            + $"'{wanted.Name}' on the source instance; it was reused as-is.");
                }
                else
                {
                    notes.Add($"snippet '{wanted.Name}' matched an existing local snippet by name.");
                }
                continue;
            }

            var created = FromBundle(wanted, takenSlugs, notes);
            takenSlugs.Add(created.Slug!);
            toCreate.Add(created);
            lookup.IdMap[wanted.Id] = created.SnippetId;

            if (wanted.NetworkEnabled)
                notes.Add(
                    $"snippet '{wanted.Name}' was network-enabled on the source instance; it was created "
                    + "WITHOUT that flag. An admin must re-enable it here after reviewing the code.");
        }

        // ── sub-workflows the graph points at ───────────────────────────
        // Every subflow_workflow_id must either be carried (§5.4) or already
        // exist here — the latter is a same-instance re-import of a bundle
        // written before sub-workflows travelled, and is worth a note.
        var carried = bundle.Dependencies.Workflows.Select(w => w.Id).ToHashSet();
        foreach (var subflowId in refs.SubflowWorkflowIds)
        {
            if (carried.Contains(subflowId)) continue;
            if (await workflows.ExistsAsync(subflowId, ct: ct))
            {
                notes.Add(
                    $"sub-workflow {subflowId} is not carried by the bundle; the subflow node was bound to "
                    + "the workflow of that id already on this instance.");
                continue;
            }
            // NOT `missing`. A sub-workflow the bundle failed to carry is a fault of the
            // SENDER (§5.4), and `bundle_dependencies_missing` blames the receiver — it tells
            // them to go create something, which is the wrong instruction: no amount of local
            // setup fixes a bundle that did not carry its own sub-workflow. Re-export it.
            incomplete.Add($"sub-workflow {subflowId} (referenced by a subflow node, not carried by the bundle)");
        }

        if (incomplete.Count > 0)
        {
            logger.LogWarning(
                "workflow.bundle.incomplete count={Count} workflow={Workflow}",
                incomplete.Count, bundle.Workflow.Name);

            throw new ValidationException(
                "the bundle does not carry everything its own graph references: "
                + string.Join("; ", incomplete)
                + ". Re-export it from the source instance; nothing you create here will fix it.",
                "bundle_incomplete",
                incomplete);
        }

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "workflow.bundle.unresolved count={Count} workflow={Workflow}",
                missing.Count, bundle.Workflow.Name);

            throw new ValidationException(
                "this instance is missing dependencies the workflow needs: "
                + string.Join("; ", missing)
                + ". Create them here (with their own credentials) and import again.",
                "bundle_dependencies_missing",
                missing);
        }

        // ── translate every graph ───────────────────────────────────────
        var untranslatable = new List<NodeReferences.Untranslatable>();
        var mainNodes = NodeReferences.Translate(bundle.Nodes, lookup, untranslatable);
        var order = WorkflowBundleReader.SubflowOrder(bundle);
        var subflows = order
            .Select(def => new ResolvedSubflow(def, NodeReferences.Translate(def.Nodes, lookup, untranslatable)))
            .ToList();

        // Plain inline ssh credentials cannot be translated to anything: a
        // password is not a reference (snippets/SPEC.md `ssh`).
        var snippetTypes = bundle.Dependencies.Snippets.ToDictionary(s => s.Id, s => s.Type);
        var sshSnippetIds = snippetTypes
            .Where(kv => string.Equals(kv.Value, SshType, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToHashSet();
        foreach (var (nodeId, key) in NodeReferences.FindPlainInlineCredentials(bundle.Nodes, sshSnippetIds))
            untranslatable.Add(new NodeReferences.Untranslatable(nodeId, key, "<plain value>"));
        foreach (var w in bundle.Dependencies.Workflows)
            foreach (var (nodeId, key) in NodeReferences.FindPlainInlineCredentials(w.Nodes, sshSnippetIds))
                untranslatable.Add(new NodeReferences.Untranslatable($"{w.Name}/{nodeId}", key, "<plain value>"));

        if (untranslatable.Count > 0)
        {
            var details = untranslatable
                .Select(u => u.Value == "<plain value>"
                    ? $"node '{u.NodeId}': '{u.Key}' is a plain value; only a ${{secret:…}} reference or a stored credential is portable"
                    : $"node '{u.NodeId}': '{u.Key}' = '{u.Value}' matches nothing on this instance")
                .ToList();
            logger.LogWarning(
                "workflow.bundle.untranslatable count={Count} workflow={Workflow}",
                details.Count, bundle.Workflow.Name);
            throw new ValidationException(
                "the bundle references things this instance cannot translate: "
                + string.Join("; ", details)
                + ". Create them here under the same name, or fix the node, and import again.",
                "bundle_reference_untranslatable",
                details);
        }

        // ── per-node notes: unknown keys, catalogued rest_call, secrets ─
        NoteNodeKeys(bundle.Nodes, null, snippetTypes, notes);
        foreach (var w in bundle.Dependencies.Workflows) NoteNodeKeys(w.Nodes, w.Name, snippetTypes, notes);

        foreach (var secret in requires.Secrets)
            notes.Add(
                $"secret reference {secret.Ref} (used by {string.Join(", ", secret.UsedBy)}) was kept verbatim. "
                + "This instance resolves ${secret:…} references only inside python_snippet payloads; other "
                + "step types receive the marker literally. Make sure the secret exists here before the first run.");

        // ── triggers ────────────────────────────────────────────────────
        var triggerDtos = await ResolveTriggersAsync(bundle.Triggers, notes, ct);

        return new BundleResolution(lookup.IdMap, toCreate, mainNodes, subflows, triggerDtos, notes);
    }

    public async Task<Guid?> FindIdenticalWorkflowAsync(
        string name, JsonElement nodes, JsonElement edges, CancellationToken ct)
    {
        var wantedNodes = CanonicalJson.Serialize(nodes);
        var wantedEdges = CanonicalJson.Serialize(edges);
        foreach (var shape in await workflows.ListActiveShapesAsync(ct))
        {
            if (!string.Equals(shape.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (CanonicalJson.Serialize(shape.Nodes) != wantedNodes) continue;
            if (CanonicalJson.Serialize(shape.Edges) != wantedEdges) continue;
            return shape.WorkflowId;
        }
        return null;
    }

    private void NoteNodeKeys(
        JsonElement nodes, string? workflowName,
        IReadOnlyDictionary<Guid, string> snippetTypes, List<string> notes)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sidEl) || sidEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(sidEl.GetString(), out var sid)
                || !snippetTypes.TryGetValue(sid, out var type)) continue;
            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object) continue;

            var nodeId = node.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
            var label = workflowName is null ? $"node '{nodeId}'" : $"node '{workflowName}/{nodeId}'";

            // A catalogued rest_call is NOT noted here: it is the
            // `rest_catalog` capability, this instance does not implement it,
            // and §2.2 makes that a refusal. It is caught above, before
            // anything is created, rather than described as a degradation that
            // would in fact be a step failing on its first run.

            // execution/SPEC.md §2: config_overrides.idempotency is an accepted
            // STRICTER-ONLY override of the snippet's tier, and importers note
            // it — a workflow's reversibility is read off its snippets, so a
            // per-node change to it must not be invisible.
            if (SnippetKeyCatalog.DeclaredIdempotency(overrides) is { } tier)
                notes.Add(
                    $"{label} declares config_overrides.idempotency = '{tier}'. It is applied as a "
                    + "stricter-only override of the snippet's tier (it can raise the tier, never lower "
                    + "it) and it affects this workflow's rollback analysis.");

            var unknown = SnippetKeyCatalog.UnknownKeys(type, overrides);
            if (unknown.Count > 0)
                notes.Add(
                    $"{label} ({type}) carries keys no known key or alias covers: {string.Join(", ", unknown)}. "
                    + "The handler ignores them — check for a typo or a product-specific extension.");
        }
    }

    // Triggers are created disabled, without targets, and signed with a fresh
    // secret (bundle/SPEC.md §6, execution/SPEC.md §7). Enabling one is an
    // explicit local act — a file must never be able to start firing a
    // workflow on someone else's instance.
    private async Task<List<CreateWorkflowTrigger>> ResolveTriggersAsync(
        IReadOnlyList<BundleTrigger> wanted, List<string> notes, CancellationToken ct)
    {
        var dtos = new List<CreateWorkflowTrigger>();
        // Same comparison the repository uses: two triggers in ONE bundle
        // whose routes differ only in case still collide on this instance.
        var routesTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in wanted)
        {
            if (string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Type))
            {
                notes.Add("a trigger without a name or type was skipped.");
                continue;
            }

            var todo = new List<string> { "set its target devices" };
            var isWebhook = string.Equals(t.Type, Models.WorkflowTrigger.TypeWebhook, StringComparison.OrdinalIgnoreCase);

            string? route = t.Route;
            if (!string.IsNullOrWhiteSpace(route)
                && (routesTaken.Contains(route) || await triggers.RouteExistsAsync(route, ct)))
            {
                var regenerated = $"{route}-{Guid.NewGuid():N}"[..Math.Min(route.Length + 9, route.Length + 33)];
                notes.Add($"trigger '{t.Name}': route '{route}' is already used on this instance; regenerated as '{regenerated}'.");
                route = regenerated;
            }
            if (!string.IsNullOrWhiteSpace(route)) routesTaken.Add(route);

            var timezone = t.Timezone;
            if (!string.IsNullOrWhiteSpace(timezone) && !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
                notes.Add(
                    $"trigger '{t.Name}': timezone '{timezone}' is not resolvable on this instance; the trigger "
                    + "stays disabled until it is corrected.");

            if (isWebhook)
            {
                todo.Add("rotate the signing secret to obtain it (the source's secret did not travel)");
                if (t.AllowUnsigned)
                    notes.Add(
                        $"trigger '{t.Name}' accepted unsigned deliveries on the source instance; here it was created "
                        + "signed, with a fresh secret. Opt back into allow_unsigned deliberately if that is wanted.");
            }
            todo.Add("enable it");

            dtos.Add(new CreateWorkflowTrigger
            {
                Name = t.Name,
                Type = t.Type,
                Description = t.Description,
                Route = route,
                CronExpression = t.CronExpression,
                Timezone = timezone,
                InputSchema = t.InputSchema,
                InputDefaults = t.InputDefaults,
                Enabled = false,
                AllowUnsigned = false,
                AllowTargetOverride = t.AllowTargetOverride,
                NotificationWebhookURL = t.NotificationWebhookUrl,
                NotifyOn = t.NotifyOn,
                TargetDevices = new List<Guid>(),
            });
            notes.Add($"trigger '{t.Name}' ({t.Type}) was created disabled and without targets — {string.Join(", ", todo)}.");
        }
        return dtos;
    }

    private async Task<(Models.Integration? Local, bool MatchedBySlug)> ResolveIntegrationAsync(
        string? slug, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await integrations.FindActiveBySlugAsync(slug, ct);
            if (bySlug is not null) return (bySlug, true);
        }

        // Name fallback covers bundles from an instance that predates slugs,
        // and the case where the same system was registered independently on
        // both sides. Exact, case-insensitive — never fuzzy: a near-miss here
        // would point a workflow at the wrong system.
        if (string.IsNullOrWhiteSpace(name)) return (null, false);
        return (await integrations.FindActiveByNameAsync(name, ct), false);
    }

    private async Task<Dictionary<string, Guid>> LocalActionsByNameAsync(Guid integrationId, CancellationToken ct)
        => (await actions.ListActiveByIntegrationAsync(integrationId, ct))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().IntegrationActionId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The local snippet a bundle snippet is reused from (bundle/SPEC.md 5.2:
    /// by slug, then by exact case-insensitive name), how it matched, and —
    /// when a row matched the identity but not the TYPE — the row that was
    /// deliberately not reused.
    /// </summary>
    /// <remarks>
    /// The type check is the whole point. A node's handler is picked by the
    /// snippet's type, so reusing a local <c>python_snippet</c> named "check"
    /// for a bundle's <c>ssh</c> snippet named "check" binds the step to
    /// unrelated local code: <c>requires.snippet_types</c> still passes, the
    /// import is clean, and the workflow runs somebody else's Python where the
    /// author wrote SSH. 5.2 says reused when FOUND, otherwise created from the
    /// carried definition — a row of the wrong type is not the snippet the
    /// bundle names, so it is not found, and the definition is created.
    /// </remarks>
    private async Task<(Models.Snippet? Local, bool MatchedBySlug, Models.Snippet? TypeMismatch)>
        ResolveSnippetAsync(BundleSnippet wanted, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(wanted.Slug))
        {
            var bySlug = await snippets.FindActiveBySlugAsync(wanted.Slug, ct);
            if (bySlug is not null)
                return SameType(bySlug, wanted) ? (bySlug, true, null) : (null, false, bySlug);
        }

        if (string.IsNullOrWhiteSpace(wanted.Name)) return (null, false, null);
        var byName = await snippets.FindActiveByNameAsync(wanted.Name, ct);
        if (byName is null) return (null, false, null);
        return SameType(byName, wanted) ? (byName, false, null) : (null, false, byName);
    }

    private static bool SameType(Models.Snippet local, BundleSnippet wanted)
        => string.Equals(local.Type, wanted.Type, StringComparison.OrdinalIgnoreCase);

    private Models.Snippet FromBundle(BundleSnippet b, IReadOnlySet<string> takenSlugs, List<string> notes)
    {
        var now = DateTime.UtcNow;

        var retry = RetryPolicyTranslation.ToCanonical(b.RetryPolicy);
        if (retry.Translated)
            notes.Add(
                $"snippet '{b.Name}': retry_policy was in the legacy {{max_attempts, delay_seconds}} shape and was "
                + "translated to {max_retries, initial_delay_seconds, backoff, max_delay_seconds: 30}.");

        return new Models.Snippet
        {
            SnippetId = Guid.NewGuid(),
            // Prefer the source slug when it is free, so the same snippet ends
            // up with the same identity on both instances and a later bundle
            // from either side resolves by slug rather than by name.
            Slug = !string.IsNullOrWhiteSpace(b.Slug) && !takenSlugs.Contains(b.Slug)
                ? b.Slug
                : Slug.Unique(b.Name, takenSlugs),
            Name = b.Name,
            Type = b.Type,
            Description = b.Description,
            Code = b.Code,
            ScriptLanguage = b.ScriptLanguage,
            InputSchema = b.InputSchema ?? default,
            OutputSchema = b.OutputSchema ?? default,
            TargetMode = b.TargetMode,
            MaxParallel = b.MaxParallel > 0 ? b.MaxParallel : 1,
            TimeoutSeconds = b.TimeoutSeconds > 0 ? b.TimeoutSeconds : 300,
            Idempotency = b.Idempotency,
            RetryPolicy = retry.Policy,
            LogicDiagramMermaid = b.LogicDiagramMermaid,
            // NEVER carried over from the bundle. network_enabled lifts the
            // python sandbox's network isolation and is admin-gated locally; a
            // file from another instance must not be able to grant it. The
            // resolution notes tell the operator it was dropped.
            NetworkEnabled = false,
            // Imported snippets are unverified here regardless of their status
            // on the source instance — this instance has not run them.
            Verified = false,
            CreatedBy = caller.Username,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static BundleSnippet ToBundleSnippet(Models.Snippet s) => new()
    {
        Id = s.SnippetId,
        Slug = s.Slug,
        Name = s.Name,
        Type = s.Type,
        Description = s.Description,
        Code = s.Code,
        ScriptLanguage = s.ScriptLanguage,
        InputSchema = s.InputSchema,
        OutputSchema = s.OutputSchema,
        TargetMode = s.TargetMode,
        MaxParallel = s.MaxParallel,
        TimeoutSeconds = s.TimeoutSeconds,
        Idempotency = s.Idempotency,
        RetryPolicy = s.RetryPolicy.ValueKind == JsonValueKind.Object ? s.RetryPolicy : null,
        LogicDiagramMermaid = s.LogicDiagramMermaid,
        NetworkEnabled = s.NetworkEnabled,
    };

    // Everything local stays behind: the encrypted secret, the target device
    // ids, the run statistics (bundle/SPEC.md §6).
    private static BundleTrigger ToBundleTrigger(Models.WorkflowTrigger t) => new()
    {
        Name = t.Name,
        Type = t.Type,
        Description = t.Description,
        Route = t.Route,
        CronExpression = t.CronExpression,
        Timezone = string.IsNullOrWhiteSpace(t.Timezone) ? null : t.Timezone,
        AllowUnsigned = t.AllowUnsigned,
        AllowTargetOverride = t.AllowTargetOverride,
        InputSchema = t.InputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : t.InputSchema,
        InputDefaults = t.InputDefaults.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : t.InputDefaults,
        Enabled = t.Enabled,
        NotifyOn = t.NotifyOn ?? new List<string>(),
        NotificationWebhookUrl = t.NotificationWebhookURL,
    };
}
