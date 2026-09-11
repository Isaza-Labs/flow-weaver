using System.Reflection;
using flow_weaver_backend.Services.Permission.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// Phase 0 of the RBAC-granular refactor (plan_rbac_granular.md). Locks the
// capability catalogue's integrity and — crucially — guards COVERAGE: every
// controller on the API surface must map to a domain the catalogue knows, so a
// newly-added endpoint can't ship without a granular capability behind it.
public class CapabilityCatalogTests
{
    // ── catalogue integrity ───────────────────────────────────────────────

    [Fact]
    public void Keys_are_unique()
    {
        var dupes = CapabilityCatalog.All
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, $"duplicate capability keys: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void Keys_are_wellformed_and_match_their_domain()
    {
        foreach (var cap in CapabilityCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(cap.Key), "capability key is blank");
            Assert.Equal(cap.Key, cap.Key.ToLowerInvariant());
            Assert.Contains('.', cap.Key);

            // Key must be `<domain>.<action...>`.
            var domainSegment = cap.Key.Split('.', 2)[0];
            Assert.Equal(cap.Domain, domainSegment);
            Assert.False(string.IsNullOrWhiteSpace(cap.Domain), $"{cap.Key} has a blank domain");
            Assert.False(string.IsNullOrWhiteSpace(cap.Description), $"{cap.Key} has a blank description");
        }
    }

    [Fact]
    public void Lookup_helpers_agree_with_the_list()
    {
        foreach (var cap in CapabilityCatalog.All)
        {
            Assert.True(CapabilityCatalog.IsKnown(cap.Key));
            Assert.Same(cap, CapabilityCatalog.Find(cap.Key));
        }

        Assert.False(CapabilityCatalog.IsKnown("does.not.exist"));
        Assert.Null(CapabilityCatalog.Find("does.not.exist"));
    }

    // ── legacy-compatibility ladder (drives the phase-1 seeder) ────────────

    [Fact]
    public void Viewer_bundle_is_a_subset_of_operator_bundle()
    {
        var viewer = CapabilityCatalog.CapabilitiesForLegacyRole("viewer").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var op = CapabilityCatalog.CapabilitiesForLegacyRole("operator").ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Subset(op, viewer);              // every viewer cap is also an operator cap
        Assert.True(op.Count > viewer.Count);   // operator strictly adds write/execute caps
    }

    [Fact]
    public void Operator_bundle_excludes_admin_tier_capabilities()
    {
        var op = CapabilitiesForLegacyRole("operator");
        Assert.DoesNotContain(op, k => CapabilityCatalog.Find(k)!.LegacyTier == LegacyTier.Admin);
    }

    [Fact]
    public void Admin_bundle_is_the_whole_catalogue()
    {
        // Admin is the documented convenience "everything"; at runtime admin
        // bypasses checks entirely and is never actually seeded a grant.
        Assert.Equal(CapabilityCatalog.All.Count, CapabilitiesForLegacyRole("admin").Count);
    }

    [Fact]
    public void Unknown_legacy_role_grants_nothing()
        => Assert.Empty(CapabilityCatalog.CapabilitiesForLegacyRole("superuser"));

    // ── coverage guard: every controller maps to a known domain ────────────

    // Sentinels for controllers that are deliberately NOT capability-gated.
    private const string Anonymous = "(anonymous)"; // signature/anon-authenticated ingress
    private const string SelfService = "(self)";    // login/refresh/me/change-password — identity, not a capability

    // Controller class name → the catalogue domain that gates it (or a
    // sentinel). Keeping this checked-in means adding a controller without a
    // capability breaks the build here, not silently in production.
    private static readonly IReadOnlyDictionary<string, string> ExpectedControllerDomains =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WorkflowController"] = "workflow",
            ["WorkflowVersionController"] = "workflow",
            ["WorkflowImportController"] = "workflow",
            ["WorkflowPlanController"] = "plan",
            ["WorkflowTriggerController"] = "trigger",
            ["RunController"] = "run",
            ["StepRunController"] = "run",
            ["DeviceController"] = "device",
            ["LoadTestController"] = "device",
            ["DevicePoolController"] = "devicepool",
            ["InventorySourceController"] = "inventory",
            ["CredentialController"] = "credential",
            ["SecretsController"] = "secret",
            ["IntegrationController"] = "integration",
            ["IntegrationTestController"] = "integration",
            ["IntegrationActionController"] = "integrationaction",
            ["McpServerController"] = "mcpserver",
            ["SnippetController"] = "snippet",
            ["SkillController"] = "skill",
            ["VendorCommandController"] = "vendorcommand",
            ["PolicyController"] = "policy",
            ["ResourcePermissionController"] = "access",
            ["PermissionGrantController"] = "access",
            ["AiChatController"] = "ai",
            ["AiConversationsController"] = "conversations",
            ["AiCatalogController"] = "aicatalog",
            ["AIAgentController"] = "aiagent",
            ["AIProviderController"] = "aiprovider",
            ["AiPromptSkillController"] = "promptskill",
            ["AiApiSpecController"] = "apispec",
            ["AllowedPythonModuleController"] = "pythonmodule",
            ["GitController"] = "git",
            ["GitWebhookCrudController"] = "gitwebhook",
            ["MessagingLinkController"] = "messaging",
            ["MessagingChannelController"] = "messaging",
            ["EmailChannelController"] = "email",
            ["ReportsController"] = "report",
            ["QaLabController"] = "qalab",
            ["JobController"] = "job",
            ["AuditController"] = "audit",
            ["TraceEventsController"] = "trace",
            ["AdminMetricsController"] = "metrics",
            ["AdminSettingsController"] = "settings",
            ["UsersController"] = "user",
            // Not capability-gated:
            ["AuthController"] = SelfService,
            // A colour theme is a personal preference — every authenticated
            // user may keep their own, so there is no capability to hold. The
            // one privileged action is publishing one to the whole org
            // (`is_shared`), and ThemeService gates that on the admin role
            // directly rather than inventing a capability for a single flag.
            ["ThemeController"] = SelfService,
            ["SchemaController"] = Anonymous,
            ["MessagingWebhookController"] = Anonymous,
            ["GitWebhookIngestController"] = Anonymous,
            ["WorkflowWebhookController"] = Anonymous,
            ["McpOAuthCallbackController"] = Anonymous,
        };

    private static IReadOnlyList<Type> DiscoverControllers() =>
        typeof(CapabilityCatalog).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && t.Namespace == "flow_weaver_backend.Controllers")
            .ToList();

    [Fact]
    public void Every_controller_is_mapped_to_a_domain_or_sentinel()
    {
        var unmapped = DiscoverControllers()
            .Select(t => t.Name)
            .Where(name => !ExpectedControllerDomains.ContainsKey(name))
            .OrderBy(n => n)
            .ToList();

        Assert.True(
            unmapped.Count == 0,
            "controllers with no capability mapping (add them to ExpectedControllerDomains and "
            + $"give them a catalogue domain): {string.Join(", ", unmapped)}");
    }

    [Fact]
    public void Mapped_domains_exist_in_the_catalogue()
    {
        var domains = CapabilityCatalog.Domains.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (controller, domain) in ExpectedControllerDomains)
        {
            if (domain is Anonymous or SelfService) continue;
            Assert.True(domains.Contains(domain),
                $"{controller} maps to domain '{domain}' which has no capabilities in the catalogue");
        }
    }

    [Fact]
    public void Mapping_has_no_stale_controllers()
    {
        var live = DiscoverControllers().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var stale = ExpectedControllerDomains.Keys.Where(name => !live.Contains(name)).OrderBy(n => n).ToList();

        Assert.True(stale.Count == 0, $"ExpectedControllerDomains lists controllers that no longer exist: {string.Join(", ", stale)}");
    }

    private static IReadOnlyList<string> CapabilitiesForLegacyRole(string role)
        => CapabilityCatalog.CapabilitiesForLegacyRole(role);
}
