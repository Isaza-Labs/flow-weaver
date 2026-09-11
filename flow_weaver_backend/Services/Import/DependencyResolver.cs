using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Import;

// Walks the proposed v1 workflow and reports every snippet /
// integration / vendor command the import references but the
// deployment does not have. Includes mapping candidates by name
// similarity so the UI can suggest "you probably meant this existing
// one".
public sealed class DependencyResolver
{
    // Built-in sentinel ids the schema validator already understands.
    private static readonly HashSet<string> Sentinels = new(StringComparer.Ordinal)
    {
        "__start__", "__end__", "subflow",
        // Built-in "virtual" snippet ids the workflow editor uses for
        // integration actions — they are dispatched by config_overrides,
        // not by a row in `snippets`.
        "integration_action",
    };

    private readonly ISnippetRepository _snippets;
    private readonly IIntegrationRepository _integrations;
    private readonly IVendorCommandRepository _vendorCommands;
    private readonly ICurrentUser _caller;

    public DependencyResolver(
        ISnippetRepository snippets,
        IIntegrationRepository integrations,
        IVendorCommandRepository vendorCommands,
        ICurrentUser caller)
    {
        _snippets = snippets;
        _integrations = integrations;
        _vendorCommands = vendorCommands;
        _caller = caller;
    }

    public async Task<DependenciesReport> ResolveAsync(
        JsonElement workflow, CancellationToken ct)
    {
        var (referencedSnippetIds, referencedIntegrationIds, referencedDeviceTypes) =
            ExtractReferences(workflow);

        var existingSnippets = await _snippets.ListActiveSummariesAsync(ct);
        var existingSnippetIds = existingSnippets.Select(s => s.SnippetId).ToHashSet();

        var existingIntegrations = await _integrations.ListActiveSummariesAsync(ct);
        var existingIntegrationIds = existingIntegrations.Select(i => i.IntegrationId).ToHashSet();

        var existingDeviceTypes = (await _vendorCommands.ListActiveDeviceTypesAsync(ct))
            .Select(d => d.ToLowerInvariant())
            .ToHashSet();

        var missingSnippets = new List<MissingSnippet>();
        foreach (var rawId in referencedSnippetIds)
        {
            if (Sentinels.Contains(rawId)) continue;

            // If the value parses as a GUID and we have it, skip.
            if (Guid.TryParse(rawId, out var guid) && existingSnippetIds.Contains(guid)) continue;

            // Heuristic for non-GUID references: match by name (the agent
            // translator deliberately emits human-readable ids when it
            // doesn't know the GUID).
            var inferredType = InferSnippetType(rawId);
            var snippetCandidates = existingSnippets
                .Where(s => string.Equals(s.Type, inferredType, StringComparison.OrdinalIgnoreCase)
                            || NameSimilarity(s.Name, rawId) > 0.4)
                .Take(5)
                .Select(s => new MappingCandidate
                {
                    Id = s.SnippetId,
                    Name = s.Name,
                    Type = s.Type,
                    SimilarityScore = NameSimilarity(s.Name, rawId),
                    Kind = "snippet",
                })
                .ToList();

            // For integration_action stubs (notify_*, alert_*, email_*,
            // ticket_*, audit_*, ...) the user usually wants to map to
            // an existing Integration (e.g. Mail, Slack, Jira) and
            // attach the action under it — not a Snippet row. Surface
            // every existing integration scored by name similarity AND
            // a small keyword bridge (so `notify_failure_stub` finds
            // `Mail`, `Slack`, etc. without needing string overlap).
            // Always surface existing-integration matches (not only for
            // integration_action inferredType) so a task that maps to a
            // registered Integration offers that mapping — and so the import
            // pipeline can skip python auto-drafting when the match is strong.
            var integrationCandidates =
                ScoreIntegrationCandidatesForActionStub(rawId, existingIntegrations).Take(5).ToList();

            var candidates = snippetCandidates
                .Concat(integrationCandidates)
                .OrderByDescending(c => c.SimilarityScore)
                .Take(5)
                .ToList();

            missingSnippets.Add(new MissingSnippet
            {
                IdInImport = rawId,
                InferredType = inferredType,
                ActionsAvailable = new[] { "stub", "generate_with_ai", "map_to_existing" },
                CandidatesForMapping = candidates,
            });
        }

        var missingIntegrations = new List<MissingIntegration>();
        foreach (var rawId in referencedIntegrationIds)
        {
            if (Guid.TryParse(rawId, out var guid) && existingIntegrationIds.Contains(guid)) continue;

            var inferredBaseUrl = ExtractInferredBaseUrl(workflow, rawId);
            var candidates = existingIntegrations
                .Where(i => NameSimilarity(i.Name, rawId) > 0.4)
                .Take(5)
                .Select(i => new MappingCandidate
                {
                    Id = i.IntegrationId,
                    Name = i.Name,
                    Type = i.Type,
                    SimilarityScore = NameSimilarity(i.Name, rawId),
                    Kind = "integration",
                })
                .OrderByDescending(c => c.SimilarityScore)
                .ToList();

            missingIntegrations.Add(new MissingIntegration
            {
                IdInImport = rawId,
                InferredBaseUrl = inferredBaseUrl,
                InferredType = "generic_rest",
                ActionsAvailable = new[] { "create_needs_config", "map_to_existing" },
                CandidatesForMapping = candidates,
            });
        }

        var missingVendor = new List<MissingVendorCommand>();
        foreach (var deviceType in referencedDeviceTypes)
        {
            if (existingDeviceTypes.Contains(deviceType.ToLowerInvariant())) continue;
            missingVendor.Add(new MissingVendorCommand
            {
                DeviceType = deviceType,
                Command = "(vendor command catalogue not seeded for this device_type)",
            });
        }

        return new DependenciesReport
        {
            Snippets = missingSnippets,
            Integrations = missingIntegrations,
            VendorCommands = missingVendor,
        };
    }

    private static (HashSet<string> Snippets, HashSet<string> Integrations, HashSet<string> DeviceTypes)
        ExtractReferences(JsonElement wf)
    {
        var snippets = new HashSet<string>(StringComparer.Ordinal);
        var integrations = new HashSet<string>(StringComparer.Ordinal);
        var deviceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return (snippets, integrations, deviceTypes);

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;

            if (node.TryGetProperty("snippet_id", out var snEl) && snEl.ValueKind == JsonValueKind.String)
            {
                var sid = snEl.GetString();
                if (!string.IsNullOrEmpty(sid)) snippets.Add(sid);
            }

            if (node.TryGetProperty("config_overrides", out var co) && co.ValueKind == JsonValueKind.Object)
            {
                if (co.TryGetProperty("integration_id", out var iid) && iid.ValueKind == JsonValueKind.String)
                {
                    var v = iid.GetString();
                    if (!string.IsNullOrEmpty(v)) integrations.Add(v);
                }
                if (co.TryGetProperty("device_type", out var dt) && dt.ValueKind == JsonValueKind.String)
                {
                    var v = dt.GetString();
                    if (!string.IsNullOrEmpty(v)) deviceTypes.Add(v);
                }
            }
        }

        return (snippets, integrations, deviceTypes);
    }

    // Keyword bridge that maps semantic concepts from a missing-snippet
    // name slug to integration name fragments that might already exist.
    // Pure string overlap (Levenshtein) wouldn't surface "Mail"
    // for "notify_failure_stub" because the names share zero letters
    // in common; this dictionary closes that gap. Conservative on
    // purpose — only well-known categories so a stray match doesn't
    // surface 12 unrelated suggestions.
    private static readonly Dictionary<string, string[]> IntegrationKeywordBridge =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["notify"]       = new[] { "mail", "smtp", "email", "slack", "teams", "sms", "pagerduty", "opsgenie", "webhook" },
            ["notification"] = new[] { "mail", "smtp", "email", "slack", "teams", "sms", "pagerduty", "opsgenie", "webhook" },
            ["alert"]        = new[] { "pagerduty", "opsgenie", "slack", "teams", "mail" },
            ["email"]        = new[] { "mail", "smtp", "outlook", "gmail", "sendgrid", "ses" },
            ["mail"]         = new[] { "mail", "smtp", "outlook", "gmail", "sendgrid", "ses" },
            ["sms"]          = new[] { "sms", "twilio", "nexmo" },
            ["chat"]         = new[] { "slack", "teams", "mattermost", "discord", "rocketchat" },
            ["ticket"]       = new[] { "jira", "servicenow", "zendesk", "freshdesk" },
            ["incident"]     = new[] { "pagerduty", "opsgenie", "servicenow" },
            ["audit"]        = new[] { "splunk", "datadog", "syslog", "elastic", "loki" },
            ["log"]          = new[] { "splunk", "datadog", "syslog", "elastic", "loki" },
            ["ipam"]         = new[] { "infoblox", "phpipam", "netbox" },
            ["dns"]          = new[] { "infoblox", "route53", "cloudflare", "dnsimple" },
            ["dhcp"]         = new[] { "infoblox", "kea", "isc" },
            ["cmdb"]         = new[] { "servicenow", "netbox", "device42" },
            ["inventory"]    = new[] { "netbox", "device42", "servicenow" },
            ["monitor"]      = new[] { "datadog", "prometheus", "grafana", "nagios", "zabbix" },
            ["backup"]       = new[] { "rubrik", "veeam", "commvault" },
        };

    // Rank existing integrations by relevance to an integration_action
    // stub the import surfaced (e.g. `notify_failure_stub`). Picks the
    // best of (a) Levenshtein name similarity and (b) keyword-bridge
    // hits, returning every integration that scores above a minimum
    // threshold so the user sees a useful shortlist even when the
    // import names a generic concept like "notify".
    private static IEnumerable<MappingCandidate> ScoreIntegrationCandidatesForActionStub(
        string idInImport, IEnumerable<IntegrationSummary> existingIntegrations)
    {
        var lowerId = idInImport.ToLowerInvariant();
        // Extract candidate keywords from the slug — split on common
        // separators and discard short noise tokens like "stub".
        var tokens = lowerId
            .Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && t is not "stub" and not "fail" and not "failure" and not "task")
            .ToArray();

        // Build the set of integration-name fragments any of those
        // tokens would bridge to (e.g. "notify" → {mail, smtp, slack, ...}).
        var bridgedFragments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            if (IntegrationKeywordBridge.TryGetValue(t, out var fragments))
                foreach (var f in fragments) bridgedFragments.Add(f);
        }

        var scored = new List<MappingCandidate>();
        foreach (var integ in existingIntegrations)
        {
            var nameSim = NameSimilarity(integ.Name, idInImport);

            // Bridge boost: 0.85 if the integration's name contains any
            // bridged fragment (case-insensitive substring), 0 otherwise.
            // 0.85 sits just above the auto-apply fuzzy threshold the
            // wizard uses for action resolution — high enough that the
            // user sees the suggestion at the top without it being so
            // close to 1.0 that we steamroll over an actual exact match.
            var bridgeBoost = bridgedFragments.Any(f =>
                integ.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) ? 0.85 : 0.0;

            // Take the stronger of the two signals — we don't want to
            // average them because a perfect name match (1.0) shouldn't
            // be pulled down by a missing bridge keyword.
            var score = Math.Max(nameSim, bridgeBoost);
            if (score < 0.3) continue;

            scored.Add(new MappingCandidate
            {
                Id = integ.IntegrationId,
                Name = integ.Name,
                Type = integ.Type,
                SimilarityScore = score,
                Kind = "integration",
            });
        }
        return scored.OrderByDescending(c => c.SimilarityScore);
    }

    // Best-effort: heuristic mapping from the imported snippet name /
    // slug to one of our handler types.
    public static string InferSnippetType(string idInImport)
    {
        var lower = idInImport.ToLowerInvariant();
        // Device / protocol built-ins first — they are specific handlers.
        if (lower.Contains("ssh") || lower.Contains("cli") || lower.Contains("exec")) return "ssh";
        if (lower.Contains("ansible") || lower.Contains("playbook")) return "ansible_playbook";
        if (lower.Contains("ping")) return "ping";
        if (lower.Contains("netconf")) return "netconf";
        if (lower.Contains("snmp")) return "snmp_v3";
        if (lower.Contains("git")) return "git";
        // A task that targets a recognised external product or sends a
        // notification is an integration, not bespoke code → integration_action,
        // so the wizard surfaces existing Integration rows to map to (and the
        // pipeline skips python auto-drafting). Runs BEFORE the generic
        // http/rest check so "netbox_api" maps to an integration, not rest_call.
        if (IntegrationActionHints.Any(h => lower.Contains(h))) return "integration_action";
        // Generic HTTP to an arbitrary URL with no recognised system behind it.
        if (lower.Contains("http") || lower.Contains("rest") || lower.Contains("api")) return "rest_call";
        if (lower.Contains("transform") || lower.Contains("jmespath")) return "transform";
        if (lower.Contains("report") || lower.Contains("export")) return "report";
        if (lower.Contains("python") || lower.Contains("script") || lower.Contains("code")) return "python_snippet";
        return "python_snippet"; // safest default: user can edit later
    }

    // Names that signal "this calls a known external system / sends a
    // notification" → integration_action. Conservative on purpose: clear
    // product names + unambiguous notification/ticketing/IPAM intents, so a
    // generic task name is not mis-routed away from python.
    private static readonly string[] IntegrationActionHints =
    {
        "notify", "notification", "alert", "email", "mail", "smtp", "slack",
        "teams", "mattermost", "sms", "twilio", "pagerduty", "opsgenie",
        "webhook", "ticket", "jira", "servicenow", "zendesk", "incident",
        "netbox", "infoblox", "ipam", "dns", "dhcp", "cmdb", "awx",
        "device42", "solarwinds",
    };

    // Pulls a base_url hint from config_overrides on nodes that reference
    // this integration id. Helps the user remember what host the import
    // was talking to.
    private static string? ExtractInferredBaseUrl(JsonElement wf, string integrationId)
    {
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("config_overrides", out var co)
                || co.ValueKind != JsonValueKind.Object) continue;
            if (!co.TryGetProperty("integration_id", out var iid)
                || iid.ValueKind != JsonValueKind.String
                || !string.Equals(iid.GetString(), integrationId, StringComparison.Ordinal))
                continue;

            if (co.TryGetProperty("base_url", out var bu) && bu.ValueKind == JsonValueKind.String)
                return bu.GetString();
            if (co.TryGetProperty("inferred_base_url", out var ibu) && ibu.ValueKind == JsonValueKind.String)
                return ibu.GetString();
        }
        return null;
    }

    // Levenshtein-based 0..1 similarity (1 = identical). Bounded to
    // small strings (snippet names are short); we cap inputs at 64 chars
    // so worst case stays O(64^2).
    private static double NameSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1.0;
        if (a.Length > 64) a = a[..64];
        if (b.Length > 64) b = b[..64];
        var dist = Levenshtein(a, b);
        var max = Math.Max(a.Length, b.Length);
        return 1.0 - (double)dist / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;
        var prev = new int[n + 1];
        var curr = new int[n + 1];
        for (var j = 0; j <= n; j++) prev[j] = j;
        for (var i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }
}
