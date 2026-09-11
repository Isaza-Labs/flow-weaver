using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using McpServerModel = flow_weaver_backend.Models.McpServer;

namespace flow_weaver_backend.Services.Validation;

public class WorkflowReferenceValidator : IWorkflowReferenceValidator
{
    private const string IntegrationActionSnippetType = "integration_action";
    private const string McpCallSnippetType = "mcp_call";

    // The only non-GUID values WorkflowExecutor.EnqueueStepAsync accepts in
    // snippet_id. Keep in step with that method: a value the engine handles but
    // this set omits would be rejected at save, and one this set allows but the
    // engine does not would fail at run time — the exact failure mode this
    // check exists to close.
    private static readonly HashSet<string> EngineSentinels =
        new(StringComparer.Ordinal) { "__start__", "__end__", "subflow" };

    private readonly IRepository<SnippetModel> _snippets;
    private readonly IRepository<IntegrationModel> _integrations;
    private readonly IRepository<IntegrationActionModel> _actions;
    private readonly IRepository<McpServerModel> _mcpServers;
    private readonly IEffectivePermissions _effective;
    private readonly IAppSettingsService _appSettings;
    private readonly ILogger<WorkflowReferenceValidator> _logger;

    public WorkflowReferenceValidator(
        IRepository<SnippetModel> snippets,
        IRepository<IntegrationModel> integrations,
        IRepository<IntegrationActionModel> actions,
        IRepository<McpServerModel> mcpServers,
        IEffectivePermissions effective,
        IAppSettingsService appSettings,
        ILogger<WorkflowReferenceValidator> logger)
    {
        _snippets = snippets;
        _integrations = integrations;
        _actions = actions;
        _mcpServers = mcpServers;
        _effective = effective;
        _appSettings = appSettings;
        _logger = logger;
    }

    public async Task<WorkflowValidationResult> ValidateAsync(
        JsonElement nodes, CancellationToken ct)
    {
        try
        {
            return await ValidateCoreAsync(nodes, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "validation.reference.failed");
            throw;
        }
    }

    public async Task<WorkflowValidationResult> ValidateWithContextAsync(
        JsonElement nodes,
        string? workflowName,
        string? workflowDescription,
        CancellationToken ct)
    {
        var baseResult = await ValidateAsync(nodes, ct);
        // Errors block: don't bother running heuristics if the DAG is
        // already broken — the user needs to fix that first.
        if (!baseResult.IsValid) return baseResult;

        var warnings = await DetectIntentMismatchesAsync(nodes, workflowName, workflowDescription, ct);
        if (warnings.Count == 0) return baseResult;

        _logger.LogInformation(
            "validation.reference.warnings count={Count}",
            warnings.Count);
        return WorkflowValidationResult.OkWithWarnings(warnings);
    }

    // Keyword → accepted snippet-type heuristic. The match is on the
    // concatenated name + description (lowercased). A workflow that
    // talks about email but ships none of the nodes that can actually
    // send one is almost certainly a python_snippet "simulating" the
    // send — we saw this exact pattern burn real users. Same pattern
    // catches "ssh" intent without an ssh node, etc.
    //
    // A hint lists EVERY node type that satisfies the intent, because
    // most of these have a native handler as well as the generic HTTP
    // route: email_send / slack_message / rest_call all became real
    // node types after this table was first written, and matching only
    // integration_action turned each of them into a false positive.
    private static readonly (string Keyword, string[] AcceptedSnippetTypes, string Message)[]
        IntentHints = new (string, string[], string)[]
        {
            ("email",  new[] { "email_send", IntegrationActionSnippetType },
             "description mentions email but no email-sending node is present. " +
             "Use the native email_send node (it delivers through a configured " +
             "SMTP channel — see /email), or integration_action for an email API. " +
             "If you're about to add a python_snippet that sends email, stop — " +
             "SnippetService rejects that pattern."),
            ("e-mail", new[] { "email_send", IntegrationActionSnippetType },
             "description mentions email but no email_send or integration_action node is present."),
            ("correo", new[] { "email_send", IntegrationActionSnippetType },
             "description mentions correo (email) but no email_send or integration_action node is present."),
            ("notify", new[] { "email_send", "slack_message", "rest_call", IntegrationActionSnippetType },
             "description mentions notify but no notification node is present. " +
             "Notifications go through email_send, slack_message, rest_call or integration_action."),
            ("slack",  new[] { "slack_message", IntegrationActionSnippetType },
             "description mentions Slack but no slack_message or integration_action node is present."),
            ("webhook", new[] { "rest_call", IntegrationActionSnippetType },
             "description mentions webhook but no rest_call or integration_action node is present."),
        };

    private async Task<List<string>> DetectIntentMismatchesAsync(
        JsonElement nodes,
        string? workflowName,
        string? workflowDescription,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        if (nodes.ValueKind != JsonValueKind.Array) return warnings;

        var haystack = ((workflowName ?? string.Empty) + " " + (workflowDescription ?? string.Empty))
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack)) return warnings;

        var snippetIds = new HashSet<Guid>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (node.TryGetProperty("snippet_id", out var sidEl)
                && sidEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(sidEl.GetString(), out var sid))
                snippetIds.Add(sid);
        }

        if (snippetIds.Count == 0) return warnings;

        var typesInUse = (await _snippets.ListByIdsAsync(snippetIds, ct: ct))
            .Select(s => s.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Collapse identical warnings when multiple keywords match the
        // same mismatch (e.g., "email" and "e-mail" both present).
        var alreadyWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyword, accepted, message) in IntentHints)
        {
            if (!haystack.Contains(keyword, StringComparison.Ordinal)) continue;
            if (accepted.Any(typesInUse.Contains)) continue;
            // Dedupe on the accepted SET, so "email" and "e-mail" collapse into
            // one warning while "slack" still gets its own.
            if (!alreadyWarned.Add(string.Join('|', accepted))) continue;
            warnings.Add(message);
        }

        return warnings;
    }

    // Tokens that signal an agent-authored scaffold placeholder landed in
    // `config_overrides` (not `snippet.Code` — SnippetService handles that).
    // We added this gate after the LLDP/DNS reconciliation incident where
    // the agent shipped a `report` node whose config had literal
    // "PENDING_RECONCILIATION", "PENDING_TAGGING" rows + a "[warn] Draft
    // scaffold" message. The step ran green, the CSV looked structurally
    // correct, and the user got a fiction-filled email. Rejecting at
    // create time is the only honest gate — the runtime has no way to
    // distinguish "template not resolved" from "author typed PENDING_".
    //
    // Matches are case-insensitive, substring-based, and only apply to
    // STRING values inside config_overrides (not keys). We skip the
    // string when it LOOKS like a valid template (`{{ steps.X.output … }}`
    // or `{{ device … }}`) because templates referencing real steps are
    // exactly the correct pattern and must not trip this gate.
    private static readonly string[] ScaffoldMarkers =
    {
        "pending_reconciliation",
        "pending_tagging",
        "pending_implementation",
        "scaffold snippet",
        "scaffold report",
        "draft scaffold",
        "placeholder row",
        "placeholder output",
        "todo: implement",
        "fixme: implement",
        "not yet implemented",
        "replace this placeholder",
    };

    private async Task<WorkflowValidationResult> ValidateCoreAsync(
        JsonElement nodes, CancellationToken ct)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
        {
            _logger.LogDebug(
                "validation.reference.ok checked_count={CheckedCount}",
                0);
            return WorkflowValidationResult.Ok();
        }

        // First pass: scan every node's config_overrides for scaffold
        // markers. Runs BEFORE the integration-action reference check so
        // a workflow that's both malformed and scaffolded gets the more
        // actionable error first.
        var scaffoldErrors = DetectScaffoldInConfigOverrides(nodes);
        if (scaffoldErrors.Count > 0)
        {
            _logger.LogWarning(
                "validation.reference.scaffold_in_config_overrides count={Count}",
                scaffoldErrors.Count);
            return WorkflowValidationResult.Invalid(scaffoldErrors);
        }

        // Placeholder snippet_ids (e.g. "REPLACE_SNIPPET_ID", "TODO",
        // "<snippet-id>") silently slip past the GUID parse below and the
        // workflow ships broken. Catching them here means the agent gets
        // a precise error instead of a runtime `invalid_snippet_id` the
        // first time someone tries to simulate the workflow.
        var placeholderErrors = DetectPlaceholderSnippetIds(nodes);
        if (placeholderErrors.Count > 0)
        {
            _logger.LogWarning(
                "validation.reference.placeholder_snippet_id count={Count}",
                placeholderErrors.Count);
            return WorkflowValidationResult.Invalid(placeholderErrors);
        }

        var integrationActionNodes = new List<(string nodeId, JsonElement configOverrides)>();
        var mcpCallNodes = new List<(string nodeId, JsonElement configOverrides)>();
        var snippetIds = new HashSet<Guid>();

        // A non-GUID snippet_id is legal ONLY for the reserved sentinels the
        // engine understands. Everything else used to be waved through here as
        // "handled elsewhere" — it wasn't, and the canvas's own editor marker
        // ("integration_action") rode that gap into the database, saved
        // cleanly, and then failed at run time as `invalid snippet_id` with no
        // error recorded anywhere the UI could show it. Reject it at save,
        // where the author is still looking at the workflow.
        var badSentinels = new List<string>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sidEl)
                || sidEl.ValueKind != JsonValueKind.String)
                continue;

            var raw = sidEl.GetString();
            if (Guid.TryParse(raw, out var snippetId))
            {
                snippetIds.Add(snippetId);
                continue;
            }

            if (EngineSentinels.Contains(raw)) continue;

            var nodeId = node.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.String
                ? nid.GetString() ?? "(unknown)"
                : "(unknown)";
            _logger.LogWarning(
                "validation.reference.bad_sentinel node_id={NodeId} snippet_id={SnippetId}", nodeId, raw);
            badSentinels.Add(
                $"node '{nodeId}': snippet_id '{raw}' is not a snippet id. "
                + $"Only {string.Join(", ", EngineSentinels.Select(s => $"'{s}'"))} may appear in its place. "
                + "Re-select the node's service in the editor and save again.");
        }

        if (badSentinels.Count > 0)
            return WorkflowValidationResult.Invalid(badSentinels);

        if (snippetIds.Count == 0)
        {
            _logger.LogDebug(
                "validation.reference.ok checked_count={CheckedCount}",
                0);
            return WorkflowValidationResult.Ok();
        }

        var snippetTypes = (await _snippets.ListByIdsAsync(snippetIds, ct: ct))
            .ToDictionary(s => s.SnippetId, s => s.Type);

        // Single pass collecting which nodes need deep validation. Runs in
        // O(nodes) — the node array is small (tens, not thousands), so a
        // nested enumeration over it is cheaper than a second index.
        var danglingRefs = new List<string>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sidEl)
                || !Guid.TryParse(sidEl.GetString(), out var snippetId))
                continue;   // sentinels (__start__/__end__) and non-GUID ids: handled elsewhere

            var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "(unknown)"
                : "(unknown)";

            // A well-formed GUID that resolves to no active snippet is a
            // dangling reference — most often a GUID the agent invented
            // instead of resolving via list_snippets. It parses past
            // the placeholder-text check above, so if left unflagged the node
            // dies at run time as the opaque `no handler for snippet type
            // 'unknown'`. Reject it here, named, so the author (or the agent,
            // reading the tool error) resolves a real id. Applies to EVERY node
            // type — a fabricated id on a report / python_snippet node fails
            // just as opaquely as one on integration_action / mcp_call.
            if (!snippetTypes.TryGetValue(snippetId, out var type))
            {
                _logger.LogWarning(
                    "validation.reference.missing node_id={NodeId} kind={Kind} ref_id={RefId}",
                    nodeId, "snippet", snippetId);
                danglingRefs.Add(
                    $"node '{nodeId}': snippet_id '{snippetId}' does not match any snippet. "
                    + "Resolve a real id with list_snippets (or the builder's palette) — never invent a GUID.");
                continue;
            }

            var isIntegration = string.Equals(type, IntegrationActionSnippetType, StringComparison.OrdinalIgnoreCase);
            var isMcp = string.Equals(type, McpCallSnippetType, StringComparison.OrdinalIgnoreCase);
            if (!isIntegration && !isMcp) continue;

            var overrides = node.TryGetProperty("config_overrides", out var co)
                            && co.ValueKind == JsonValueKind.Object
                ? co
                : default;

            if (isIntegration) integrationActionNodes.Add((nodeId, overrides));
            else mcpCallNodes.Add((nodeId, overrides));
        }

        // Dangling snippet refs are fatal on their own — return before the
        // integration/mcp short-circuit below, which would otherwise report OK
        // for a workflow whose every node points at a fabricated id.
        if (danglingRefs.Count > 0)
        {
            _logger.LogWarning(
                "validation.reference.dangling_snippet_ids count={Count}",
                danglingRefs.Count);
            return WorkflowValidationResult.Invalid(danglingRefs);
        }

        if (integrationActionNodes.Count == 0 && mcpCallNodes.Count == 0)
        {
            _logger.LogDebug(
                "validation.reference.ok checked_count={CheckedCount}",
                snippetIds.Count);
            return WorkflowValidationResult.Ok();
        }

        var errors = new List<string>();

        // ── integration_action nodes ─────────────────────────────────────
        if (integrationActionNodes.Count > 0)
        {
            var integrationIds = new HashSet<Guid>();
            var actionIds = new HashSet<Guid>();
            var parsed = new List<(string nodeId, Guid integrationId, Guid actionId)>();

            foreach (var (nodeId, overrides) in integrationActionNodes)
            {
                if (overrides.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"node '{nodeId}': integration_action requires config_overrides with integration_id and action_id (both GUIDs). Use list_palette_actions to resolve the real ids.");
                    continue;
                }

                var integrationOk = TryReadGuid(overrides, "integration_id", out var integrationId, out var integrationErr);
                var actionOk = TryReadGuid(overrides, "action_id", out var actionId, out var actionErr);

                if (!integrationOk) errors.Add($"node '{nodeId}': {integrationErr}");
                if (!actionOk) errors.Add($"node '{nodeId}': {actionErr}");
                if (!integrationOk || !actionOk) continue;

                integrationIds.Add(integrationId);
                actionIds.Add(actionId);
                parsed.Add((nodeId, integrationId, actionId));
            }

            if (integrationIds.Count > 0 || actionIds.Count > 0)
            {
                var integrationSet = (await _integrations.ListByIdsAsync(integrationIds, ct: ct))
                    .Select(i => i.IntegrationId)
                    .ToHashSet();

                var actionMap = (await _actions.ListByIdsAsync(actionIds, ct: ct))
                    .ToDictionary(a => a.IntegrationActionId, a => a.IntegrationId);

                foreach (var (nodeId, integrationId, actionId) in parsed)
                {
                    if (!integrationSet.Contains(integrationId))
                    {
                        _logger.LogWarning(
                            "validation.reference.missing node_id={NodeId} kind={Kind} ref_id={RefId}",
                            nodeId, "integration", integrationId);
                        errors.Add($"node '{nodeId}': integration_id '{integrationId}' not found or inactive.");
                    }

                    if (!actionMap.TryGetValue(actionId, out var actionOwner))
                    {
                        _logger.LogWarning(
                            "validation.reference.missing node_id={NodeId} kind={Kind} ref_id={RefId}",
                            nodeId, "integration_action", actionId);
                        errors.Add($"node '{nodeId}': action_id '{actionId}' not found or inactive.");
                    }
                    else if (actionOwner != integrationId)
                    {
                        _logger.LogWarning(
                            "validation.reference.missing node_id={NodeId} kind={Kind} ref_id={RefId}",
                            nodeId, "integration_action_mismatch", actionId);
                        errors.Add($"node '{nodeId}': action_id '{actionId}' belongs to integration '{actionOwner}', not '{integrationId}'.");
                    }
                }
            }
        }

        // ── mcp_call nodes ───────────────────────────────────────────────
        if (mcpCallNodes.Count > 0)
        {
            var serverIds = new HashSet<Guid>();
            var parsedMcp = new List<(string nodeId, Guid serverId, string? toolName)>();

            foreach (var (nodeId, overrides) in mcpCallNodes)
            {
                if (overrides.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"node '{nodeId}': mcp_call requires config_overrides with mcp_server_id (a GUID) and tool_name.");
                    continue;
                }

                var serverOk = false;
                var serverId = Guid.Empty;
                if (!overrides.TryGetProperty("mcp_server_id", out var sv)
                    || sv.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    errors.Add($"node '{nodeId}': config_overrides.mcp_server_id is required for mcp_call nodes.");
                }
                else if (sv.ValueKind != JsonValueKind.String || !Guid.TryParse(sv.GetString(), out serverId))
                {
                    errors.Add($"node '{nodeId}': config_overrides.mcp_server_id must be a GUID. Use the builder's MCP section or list_mcp_servers to resolve the real id.");
                }
                else
                {
                    serverOk = true;
                }

                // `tool` is the CANONICAL spelling (snippets/SPEC.md
                // `mcp_call`); `tool_name` is what this engine used to read and
                // is still accepted as an alias. The precedence has to match
                // PayloadAliases exactly — validating one name while the
                // handler calls the other is how a node passes validation and
                // then invokes a tool nobody checked.
                string? toolName = null;
                if (overrides.TryGetProperty("tool", out var toolCanonical)
                    && toolCanonical.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(toolCanonical.GetString()))
                {
                    toolName = toolCanonical.GetString();
                }
                else if (overrides.TryGetProperty("tool_name", out var tn)
                    && tn.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(tn.GetString()))
                {
                    toolName = tn.GetString();
                }
                else
                {
                    errors.Add($"node '{nodeId}': config_overrides.tool is required for mcp_call nodes.");
                }

                if (serverOk)
                {
                    serverIds.Add(serverId);
                    parsedMcp.Add((nodeId, serverId, toolName));
                }
            }

            if (serverIds.Count > 0)
            {
                var serverSet = (await _mcpServers.ListByIdsAsync(serverIds, ct: ct))
                    .Select(s => s.McpServerId)
                    .ToHashSet();

                foreach (var (nodeId, serverId, _) in parsedMcp)
                {
                    if (!serverSet.Contains(serverId))
                    {
                        _logger.LogWarning(
                            "validation.reference.missing node_id={NodeId} kind={Kind} ref_id={RefId}",
                            nodeId, "mcp_server", serverId);
                        errors.Add($"node '{nodeId}': mcp_server_id '{serverId}' not found or inactive.");
                    }
                }
            }

            // Granular RBAC: the author must hold mcp.execute for each referenced
            // (server, tool). This mirrors the agent's call_mcp_tool gate so the
            // per-server / per-tool grant conditions can't be bypassed by
            // authoring a workflow. Legacy mode relies on the coarse role gate.
            var settings = await _appSettings.GetAsync(ct);
            if (RbacModes.IsGranular(settings.RbacMode))
            {
                foreach (var (nodeId, serverId, toolName) in parsedMcp)
                {
                    if (string.IsNullOrEmpty(toolName)) continue; // already flagged structurally
                    var permCtx = new PermissionContext(McpServerId: serverId, McpToolName: toolName);
                    if (!await _effective.HasAsync("mcp.execute", permCtx, ct))
                    {
                        _logger.LogWarning(
                            "validation.reference.mcp_denied node_id={NodeId} server={Server} tool={Tool}",
                            nodeId, serverId, toolName);
                        errors.Add($"node '{nodeId}': you are not authorized to call tool '{toolName}' on this MCP server (mcp.execute).");
                    }
                }
            }
        }

        if (errors.Count == 0)
        {
            _logger.LogDebug(
                "validation.reference.ok checked_count={CheckedCount}",
                integrationActionNodes.Count + mcpCallNodes.Count);
            return WorkflowValidationResult.Ok();
        }
        return WorkflowValidationResult.Invalid(errors);
    }

    private static bool TryReadGuid(JsonElement el, string key, out Guid value, out string error)
    {
        value = Guid.Empty;
        error = string.Empty;
        if (!el.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null
            || v.ValueKind == JsonValueKind.Undefined)
        {
            error = $"config_overrides.{key} is required for integration_action nodes.";
            return false;
        }
        if (v.ValueKind != JsonValueKind.String || !Guid.TryParse(v.GetString(), out value))
        {
            var raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            error = $"config_overrides.{key} must be a GUID (got '{raw}'). Use list_palette_actions to resolve the real id.";
            return false;
        }
        return true;
    }

    // Recursively walks every node's `config_overrides`, collecting any
    // string value that contains a scaffold marker. Template expressions
    // (`{{ steps.X.output … }}`, `{{ device … }}`) are skipped because a
    // workflow that correctly references a previous step's output is the
    // pattern we WANT — not a scaffold.
    //
    // Returns one error per offending node+path so the agent can fix each
    // independently instead of guessing which field is wrong.
    private static List<string> DetectScaffoldInConfigOverrides(JsonElement nodes)
    {
        var errors = new List<string>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("config_overrides", out var overrides)
                || overrides.ValueKind != JsonValueKind.Object)
                continue;

            var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "(unknown)"
                : "(unknown)";

            ScanForScaffolds(overrides, path: string.Empty, nodeId: nodeId, errors);
        }
        return errors;
    }

    // Reserved sentinel values the workflow schema accepts in `snippet_id`
    // in addition to GUIDs — see workflow.v1.schema.json and Dag.cs.
    // `integration_action` is a virtual snippet id: the runtime dispatches
    // the node via `config_overrides.integration_id` + `action_id` and
    // never looks up a Snippet row. Keep this set in sync with
    // DependencyResolver.Sentinels — divergence between the two caused
    // a 228-node import to fail with "looks like a placeholder" when the
    // analyse phase had already skipped the same value as a sentinel.
    private static readonly HashSet<string> ReservedSnippetIds = new(StringComparer.Ordinal)
    {
        "__start__", "__end__", "subflow", "integration_action",
    };

    // Flags any node whose `snippet_id` is a non-empty string that isn't
    // a parseable GUID and isn't one of the reserved sentinel values.
    // The agent sometimes emits placeholders like "REPLACE_SNIPPET_ID"
    // when batching create_snippet + create_workflow in a single plan
    // and forgets to chain the returned id. Catching that here gives a
    // specific fix-it message instead of a simulate-time
    // `invalid_snippet_id` several steps later.
    private static List<string> DetectPlaceholderSnippetIds(JsonElement nodes)
    {
        var errors = new List<string>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sidEl)) continue;
            if (sidEl.ValueKind != JsonValueKind.String) continue;

            var raw = sidEl.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (ReservedSnippetIds.Contains(raw)) continue;
            if (Guid.TryParse(raw, out _)) continue;

            var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "(unknown)"
                : "(unknown)";
            errors.Add(
                $"node '{nodeId}': snippet_id '{raw}' is not a GUID. Looks like a placeholder — " +
                "after calling create_snippet, use the returned `snippet_id` from its response " +
                "(not a literal like 'REPLACE_SNIPPET_ID' or 'TODO') when wiring the workflow node.");
        }
        return errors;
    }

    private static void ScanForScaffolds(
        JsonElement el, string path, string nodeId, List<string> errors)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = el.GetString() ?? string.Empty;
                if (LooksLikeTemplate(s)) return;
                foreach (var marker in ScaffoldMarkers)
                {
                    if (s.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(
                            $"node '{nodeId}' config_overrides{path}: contains scaffold marker '{marker}'. " +
                            "This looks like a placeholder from an incomplete workflow — implement the " +
                            "real logic (likely a python_snippet that calls integration(...)) or remove " +
                            "the step. Shipping scaffolds lands fake data in downstream reports.");
                        return; // one error per string is enough
                    }
                }
                return;
            }
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    ScanForScaffolds(prop.Value, $"{path}.{prop.Name}", nodeId, errors);
                return;
            case JsonValueKind.Array:
            {
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    ScanForScaffolds(item, $"{path}[{i++}]", nodeId, errors);
                return;
            }
            default:
                return;
        }
    }

    // A string that is exactly or mostly a Jinja-style template is the
    // correct shape for cross-step references, not a scaffold. Even when
    // the path the agent used is wrong (e.g. referencing a field that
    // does not exist), that's a resolver-warning case at runtime — not
    // something to reject at workflow-create time, because the agent
    // might later fix the shape via update_workflow_node_config.
    private static bool LooksLikeTemplate(string s)
    {
        var trimmed = s.Trim();
        return trimmed.Contains("{{", StringComparison.Ordinal)
            && trimmed.Contains("}}", StringComparison.Ordinal);
    }
}
