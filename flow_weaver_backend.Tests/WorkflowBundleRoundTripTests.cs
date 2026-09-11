using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The scenario this format exists for: export a workflow on instance A, import
// it on instance B, where the SAME integration is registered — under different
// GUIDs, because GUIDs are per-instance.
//
// Before the bundle, that produced placeholder python snippets. The v1 export
// carried only GUIDs, none of them resolved on B, every dependency read as
// missing, and the fuzzy matcher scored those GUID strings against integration
// names and matched nothing — so the only remedy the wizard could offer was a
// stub. These tests pin the behaviour that replaced it.
public class WorkflowBundleRoundTripTests
{
    private const string NetboxSlug = "netbox";

    private static WorkflowBundleService Svc(AppDbContext db) => BundleTest.Svc(db);

    // ── instance setup ────────────────────────────────────────────────────

    private static (Guid IntegrationId, Guid ActionId) SeedIntegration(
        AppDbContext db, string name = "NetBox", string? slug = NetboxSlug,
        string actionName = "list_devices")
    {
        var integrationId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        db.Integrations.Add(new Integration
        {
            IntegrationId = integrationId,
            Slug = slug,
            Name = name,
            Type = "generic_rest",
            BaseURL = "https://netbox.example.test",
            IsActive = true,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = actionId,
            IntegrationId = integrationId,
            Name = actionName,
            Method = "GET",
            Path = "/api/dcim/devices/",
            Category = "dcim",
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return (integrationId, actionId);
    }

    private static Guid SeedSnippet(AppDbContext db, string name = "Build Rows", string? slug = "build-rows")
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = id,
            Slug = slug,
            Name = name,
            Type = "python_snippet",
            Code = "set_output({'rows': []})",
            ScriptLanguage = "python",
            TargetMode = "once",
            MaxParallel = 1,
            TimeoutSeconds = 60,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedWorkflow(AppDbContext db, Guid snippetId, Guid integrationId, Guid actionId)
    {
        var id = Guid.NewGuid();
        var nodes = JsonDocument.Parse($$"""
            [
              { "id": "__start__", "snippet_id": "__start__", "x": 0, "y": 0 },
              { "id": "build", "snippet_id": "{{snippetId}}", "x": 220, "y": 0 },
              { "id": "fetch", "snippet_id": "{{snippetId}}", "x": 440, "y": 0,
                "config_overrides": {
                  "integration_id": "{{integrationId}}",
                  "action_id": "{{actionId}}",
                  "note": "kept verbatim"
                } }
            ]
            """).RootElement.Clone();
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = id,
            Name = "inventory-report",
            Description = "d",
            Environment = "draft",
            Nodes = nodes,
            Edges = JsonDocument.Parse("[]").RootElement.Clone(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    // ── export ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_describes_every_referenced_dependency_portably()
    {
        using var db = TestDb.NewContext();
        var (integrationId, actionId) = SeedIntegration(db);
        var snippetId = SeedSnippet(db);
        var wfId = SeedWorkflow(db, snippetId, integrationId, actionId);

        var bundle = await Svc(db).BuildAsync(wfId, default);

        Assert.Equal(WorkflowBundle.CurrentSchemaVersion, bundle.SchemaVersion);
        Assert.Equal(WorkflowBundle.KindMarker, bundle.Kind);

        var integration = Assert.Single(bundle.Dependencies.Integrations);
        Assert.Equal(NetboxSlug, integration.Slug);
        Assert.Equal("NetBox", integration.Name);
        Assert.Equal("list_devices", Assert.Single(integration.Actions).Name);

        var snippet = Assert.Single(bundle.Dependencies.Snippets);
        Assert.Equal("build-rows", snippet.Slug);
        // The BODY travels — that is what lets the far side recreate the
        // snippet faithfully instead of stubbing it.
        Assert.Equal("set_output({'rows': []})", snippet.Code);
    }

    [Fact]
    public async Task Export_never_carries_integration_credentials()
    {
        // The receiving instance uses its OWN credentials. A bundle is passed
        // around in chat and committed to git; a secret in it is a secret leak.
        using var db = TestDb.NewContext();
        var (integrationId, actionId) = SeedIntegration(db);
        db.Integrations.Single().AuthConfig =
            JsonDocument.Parse("""{"token":"super-secret-value"}""").RootElement.Clone();
        db.SaveChanges();
        var wfId = SeedWorkflow(db, SeedSnippet(db), integrationId, actionId);

        var json = JsonSerializer.Serialize(
            await Svc(db).BuildAsync(wfId, default), WorkflowBundleReader.SerializerOptions);

        Assert.DoesNotContain("super-secret-value", json);
        Assert.DoesNotContain("auth_config", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_only_carries_the_actions_the_workflow_uses()
    {
        using var db = TestDb.NewContext();
        var (integrationId, actionId) = SeedIntegration(db);
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(),
            IntegrationId = integrationId,
            Name = "delete_everything",
            Method = "DELETE", Path = "/api/x", Category = "danger",
            Enabled = true, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var wfId = SeedWorkflow(db, SeedSnippet(db), integrationId, actionId);

        var bundle = await Svc(db).BuildAsync(wfId, default);

        Assert.Equal("list_devices", Assert.Single(bundle.Dependencies.Integrations[0].Actions).Name);
    }

    // ── import into a DIFFERENT instance ──────────────────────────────────

    [Fact]
    public async Task Import_binds_to_the_local_integration_with_different_guids()
    {
        const string testName = "Import_binds_to_the_local_integration_with_different_guids";
        // The reported failure, as a test. Instance B holds the same NetBox
        // under its own GUIDs; the bundle must resolve to THEM.
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var srcSnippet = SeedSnippet(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, srcSnippet, srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        var (dstIntegration, dstAction) = SeedIntegration(target);
        Assert.NotEqual(srcIntegration, dstIntegration);   // genuinely different instance

        var resolution = await Svc(target).ResolveAsync(bundle, default);

        Assert.Equal(dstIntegration, resolution.IdMap[srcIntegration]);
        Assert.Equal(dstAction, resolution.IdMap[srcAction]);

        // And the nodes the importer will store now point at the local rows.
        // CONTRACT CHANGE (bundle/SPEC.md §4): the bundle itself no longer
        // carries `integration_id` / `action_id` — it names the integration by
        // slug and the action by name — so the local ids come from the
        // resolved graph the importer produces, not from remapping the wire
        // form. The wire form is checked here too: it must be names only.
        var wire = bundle.Nodes.EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "fetch")
            .GetProperty("config_overrides");
        Assert.Equal(NetboxSlug, wire.GetProperty("integration").GetString());
        Assert.Equal("list_devices", wire.GetProperty("action").GetString());
        Assert.False(wire.TryGetProperty("integration_id", out _));
        Assert.False(wire.TryGetProperty("action_id", out _));

        var remapped = NodeReferences.Remap(resolution.Nodes, resolution.IdMap);
        var fetch = remapped.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "fetch");
        var overrides = fetch.GetProperty("config_overrides");
        Assert.Equal(dstIntegration.ToString(), overrides.GetProperty("integration_id").GetString());
        Assert.Equal(dstAction.ToString(), overrides.GetProperty("action_id").GetString());
        // Unrelated override keys survive untouched.
        Assert.Equal("kept verbatim", overrides.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Import_recreates_a_missing_snippet_from_its_definition_not_a_stub()
    {
        const string testName = "Import_recreates_a_missing_snippet_from_its_definition_not_a_stub";
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, SeedSnippet(source), srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        SeedIntegration(target);

        var resolution = await Svc(target).ResolveAsync(bundle, default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal("Build Rows", created.Name);
        Assert.Equal("python_snippet", created.Type);
        // The real body, not the "placeholder": True" stub the wizard used to
        // fabricate. This is the whole point of the format.
        Assert.Equal("set_output({'rows': []})", created.Code);
        Assert.DoesNotContain("placeholder", created.Code!);
    }

    [Fact]
    public async Task Import_reuses_a_snippet_the_target_already_has()
    {
        const string testName = "Import_reuses_a_snippet_the_target_already_has";
        // Idempotency: importing twice, or importing a workflow that shares
        // building blocks with one already here, must not clone them.
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, SeedSnippet(source), srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        SeedIntegration(target);
        var localSnippet = SeedSnippet(target);   // same slug, different GUID

        var resolution = await Svc(target).ResolveAsync(bundle, default);

        Assert.Empty(resolution.SnippetsToCreate);
        Assert.Equal(localSnippet, resolution.IdMap.Values.Single(v => v == localSnippet));
    }

    [Fact]
    public async Task Import_matches_by_name_when_the_target_predates_slugs()
    {
        const string testName = "Import_matches_by_name_when_the_target_predates_slugs";
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, SeedSnippet(source), srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        var (dstIntegration, _) = SeedIntegration(target, slug: null);   // no slug yet

        var resolution = await Svc(target).ResolveAsync(bundle, default);

        Assert.Equal(dstIntegration, resolution.IdMap[srcIntegration]);
        Assert.Contains(resolution.Notes, n => n.Contains("matched by name"));
    }

    // ── the failure the user asked for: say so, don't substitute ───────────

    [Fact]
    public async Task A_missing_integration_aborts_and_names_it()
    {
        const string testName = "A_missing_integration_aborts_and_names_it";
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, SeedSnippet(source), srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);   // no NetBox here

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => Svc(target).ResolveAsync(bundle, default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("NetBox", ex.Message);
        Assert.Contains("netbox", ex.Message);          // the slug, so it is actionable
        Assert.NotNull(ex.Details);
        Assert.NotEmpty(ex.Details!);
    }

    [Fact]
    public async Task A_missing_action_aborts_even_when_the_integration_exists()
    {
        const string testName = "A_missing_action_aborts_even_when_the_integration_exists";
        // The usual cause is an older OpenAPI spec on the target. Naming the
        // action is what tells the operator to re-upload it.
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, SeedSnippet(source), srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        SeedIntegration(target, actionName: "something_else");

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => Svc(target).ResolveAsync(bundle, default));

        Assert.Contains("list_devices", ex.Message);
    }

    // ── capability must not travel ─────────────────────────────────────────

    [Fact]
    public async Task A_network_enabled_snippet_is_recreated_without_that_flag()
    {
        const string testName = "A_network_enabled_snippet_is_recreated_without_that_flag";
        // network_enabled lifts the python sandbox's network isolation and is
        // admin-gated locally. A file from another instance must not be able to
        // grant itself that — but the operator has to be told it was dropped.
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var snippetId = SeedSnippet(source);
        source.Snippets.Single().NetworkEnabled = true;
        source.SaveChanges();
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, snippetId, srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        SeedIntegration(target);

        var resolution = await Svc(target).ResolveAsync(bundle, default);

        Assert.False(Assert.Single(resolution.SnippetsToCreate).NetworkEnabled);
        Assert.Contains(resolution.Notes, n => n.Contains("network-enabled"));
    }

    [Fact]
    public async Task An_imported_snippet_is_not_marked_verified()
    {
        const string testName = "An_imported_snippet_is_not_marked_verified";
        using var source = TestDb.NewContext();
        var (srcIntegration, srcAction) = SeedIntegration(source);
        var snippetId = SeedSnippet(source);
        source.Snippets.Single().Verified = true;
        source.SaveChanges();
        var bundle = await Svc(source).BuildAsync(
            SeedWorkflow(source, snippetId, srcIntegration, srcAction), default);

        using var target = TestDb.NewContext("instance-b-" + testName);
        SeedIntegration(target);

        // This instance has never run it, so its verification status here is
        // false regardless of what the source claimed.
        Assert.False(Assert.Single(
            (await Svc(target).ResolveAsync(bundle, default)).SnippetsToCreate).Verified);
    }

    // ── format recognition ────────────────────────────────────────────────

    [Fact]
    public async Task A_bundle_is_recognised_by_its_marker()
    {
        using var db = TestDb.NewContext();
        var (i, a) = SeedIntegration(db);
        var json = JsonSerializer.Serialize(
            await Svc(db).BuildAsync(SeedWorkflow(db, SeedSnippet(db), i, a), default),
            WorkflowBundleReader.SerializerOptions);

        Assert.True(WorkflowBundleReader.LooksLikeBundle(json, "json"));
    }

    [Theory]
    // The legacy v1 export: nodes + edges, no marker. Must NOT be taken for a
    // bundle, or it would hit the deterministic path with no dependencies
    // section and lose every reference.
    [InlineData("""{"version":1,"workflow":{"name":"x"},"nodes":[],"edges":[]}""")]
    [InlineData("""{"nodes":[],"edges":[]}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Non_bundles_are_not_recognised(string raw)
    {
        Assert.False(WorkflowBundleReader.LooksLikeBundle(raw, "json"));
    }

    [Fact]
    public void An_unknown_schema_version_is_refused_with_the_version_in_the_message()
    {
        var raw = $$$"""
            {"schema_version":"v99","kind":"{{{WorkflowBundle.KindMarker}}}",
             "workflow":{"name":"x"},"nodes":[],"edges":[],
             "dependencies":{"snippets":[],"integrations":[]}}
            """;

        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));

        Assert.Equal("bundle_version_unsupported", ex.Code);
        Assert.Contains("v99", ex.Message);
    }

    [Fact]
    public void A_bundle_referencing_a_snippet_it_does_not_carry_is_refused()
    {
        // Completing this by guessing is exactly the placeholder behaviour the
        // format removes, so an incomplete file is rejected instead.
        var orphan = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v2","kind":"{{{WorkflowBundle.KindMarker}}}",
             "workflow":{"name":"x"},
             "nodes":[{"id":"n","snippet_id":"{{{orphan}}}"}],"edges":[],
             "dependencies":{"snippets":[],"integrations":[]}}
            """;

        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));

        Assert.Equal("bundle_incomplete", ex.Code);
        Assert.Contains(orphan.ToString(), ex.Message);
    }

    [Fact]
    public void Sentinel_nodes_survive_remapping()
    {
        // __start__ / __end__ carry non-GUID snippet ids; the remapper must
        // leave anything it cannot parse alone rather than dropping it.
        var nodes = JsonDocument.Parse(
            """[{"id":"__start__","snippet_id":"__start__"}]""").RootElement.Clone();

        var remapped = NodeReferences.Remap(nodes, new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() });

        Assert.Equal("__start__", remapped.EnumerateArray().Single().GetProperty("snippet_id").GetString());
    }
}
