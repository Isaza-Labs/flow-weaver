using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Workflow;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using TriggerModel = flow_weaver_backend.Models.WorkflowTrigger;

namespace flow_weaver_backend.Tests;

// v3 of the interchange bundle (bundle/SPEC.md). v2 gave every referenced GUID
// a portable identity and stopped there: it declared nothing about what the
// receiving engine had to support, it wrote ids where a name was the only
// thing that meant anything on the far side, and it silently dropped the two
// pieces a real workflow is usually made of — its sub-workflows and its
// triggers. A bundle that drops half the workflow still imports, which is the
// failure this file exists to make impossible.
public class WorkflowBundleV3Tests
{
    // ── instance fixtures ─────────────────────────────────────────────────

    private static Guid SeedSnippet(
        AppDbContext db, string name, string slug, string type = "python_snippet",
        string targetMode = "once", int maxParallel = 1, string? code = "pass")
    {
        var id = Guid.NewGuid();
        db.Snippets.Add(new SnippetModel
        {
            SnippetId = id,
            Slug = slug,
            Name = name,
            Type = type,
            Code = code,
            ScriptLanguage = type == "python_snippet" ? "python" : null,
            TargetMode = targetMode,
            MaxParallel = maxParallel,
            TimeoutSeconds = 60,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedMcpServer(AppDbContext db, string name = "netops-mcp")
    {
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id,
            Name = name,
            Url = "https://mcp.example.test",
            Transport = "http",
            AuthType = "bearer",
            AuthConfigEncrypted = System.Text.Encoding.UTF8.GetBytes("mcp-token-should-never-travel"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedCredential(AppDbContext db, string name = "netops-ssh")
    {
        var id = Guid.NewGuid();
        db.Credentials.Add(new Credential
        {
            CredentialId = id,
            Name = name,
            Type = "ssh",
            AuthMethod = Credential.AuthMethodPassword,
            Username = "netops",
            EncryptedPassword = System.Text.Encoding.UTF8.GetBytes("password-should-never-travel"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedRepository(AppDbContext db, string name = "configs")
    {
        var id = Guid.NewGuid();
        db.GitRepositories.Add(new GitRepository
        {
            GitRepositoryId = id,
            Name = name,
            Url = "https://git.example.test/netops/configs.git",
            DefaultBranch = "main",
            AuthCredentialId = Guid.NewGuid(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedWorkflow(
        AppDbContext db, string name, string nodes, string edges = "[]", string? metadata = null)
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = id,
            Name = name,
            Description = name + " description",
            Environment = "draft",
            Nodes = BundleTest.Json(nodes),
            Edges = BundleTest.Json(edges),
            Metadata = metadata is null ? default : BundleTest.Json(metadata),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static void SeedTrigger(
        AppDbContext db, Guid workflowId, string name, string type = TriggerModel.TypeWebhook,
        string? route = "abc123", bool enabled = true)
    {
        db.Set<TriggerModel>().Add(new TriggerModel
        {
            WorkflowTriggerId = Guid.NewGuid(),
            WorkflowId = workflowId,
            Name = name,
            Type = type,
            Route = route,
            EncryptedSecret = System.Text.Encoding.UTF8.GetBytes("hmac-secret-should-never-travel"),
            AllowUnsigned = false,
            AllowTargetOverride = true,
            CronExpression = type == "cron" ? "0 3 * * *" : null,
            Timezone = type == "cron" ? "America/Bogota" : string.Empty,
            Enabled = enabled,
            LastRunAt = DateTime.UtcNow,
            LastRunStatus = "completed",
            NotifyOn = new List<string> { "failed" },
            TargetDevices = new List<Guid> { Guid.NewGuid() },
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── §1 root, §2 requires ──────────────────────────────────────────────

    [Fact]
    public async Task Export_stamps_v3_and_says_who_wrote_it()
    {
        using var db = TestDb.NewContext();
        var snippet = SeedSnippet(db, "Collect", "collect");
        var wf = SeedWorkflow(db, "audit", $$$"""[{"id":"a","snippet_id":"{{{snippet}}}"}]""");

        var bundle = await BundleTest.Svc(db).BuildAsync(wf, default);

        Assert.Equal("v3", bundle.SchemaVersion);
        Assert.Equal("flow_weaver.workflow_bundle", bundle.Kind);
        Assert.Equal("flow-weaver", bundle.ExportedBy!.Product);
        Assert.False(string.IsNullOrWhiteSpace(bundle.ExportedBy.Version));
        // requires is derived from the graph, never asserted: a workflow that
        // needs nothing special declares nothing special.
        Assert.Equal(["python_snippet"], bundle.Requires!.SnippetTypes);
        Assert.Empty(bundle.Requires.Capabilities);
        Assert.Empty(bundle.Requires.Secrets);
    }

    [Fact]
    public async Task Export_derives_every_capability_the_graph_actually_uses()
    {
        using var db = TestDb.NewContext();
        var parallel = SeedSnippet(db, "Fan Out", "fan-out", targetMode: "per_device", maxParallel: 4);
        var pool = SeedSnippet(db, "Pooled", "pooled", targetMode: "per_pool");
        var child = SeedWorkflow(db, "child", "[]");
        var nodes = $$$"""
            [
              { "id": "show", "snippet_id": "{{{parallel}}}", "config_overrides": {} },
              { "id": "pooled", "snippet_id": "{{{pool}}}", "config_overrides": {} },
              { "id": "read", "snippet_id": "{{{parallel}}}",
                "config_overrides": { "text": "{{ steps.show.output.stdout | upper }}", "run": "{{ run.id }}" } },
              { "id": "sub", "snippet_id": "subflow", "type": "subflow",
                "config_overrides": { "subflow_workflow_id": "{{{child}}}" } }
            ]
            """;
        var edges = """[{"id":"e1","source":"show","target":"read","type":"conditional","condition":"{{ steps.show.output.ok }} == true"}]""";
        var wf = SeedWorkflow(db, "everything", nodes, edges);
        SeedTrigger(db, wf, "nightly", type: "cron", route: null);

        var bundle = await BundleTest.Svc(db).BuildAsync(wf, default);

        Assert.Equal(
            [
                "conditional_edges", "max_parallel", "per_device_scope", "per_pool",
                "run_namespace", "subflow", "template_filters", "triggers",
            ],
            bundle.Requires!.Capabilities);
        // Every name is inside the closed vocabulary, so this instance can read
        // back what it wrote.
        Assert.All(bundle.Requires.Capabilities, c => Assert.Contains(c, BundleCapabilities.Known));
    }

    [Fact]
    public void An_unknown_capability_is_refused_by_name()
    {
        var raw = """
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle",
             "workflow":{"name":"x"},"nodes":[],"edges":[],
             "requires":{"snippet_types":[],"capabilities":["quantum_edges"],"secrets":[]},
             "dependencies":{}}
            """;

        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));

        Assert.Equal("bundle_capability_unsupported", ex.Code);
        Assert.Contains("quantum_edges", ex.Message);
    }

    [Fact]
    public void Every_accepted_kind_parses_and_an_unknown_one_does_not()
    {
        foreach (var kind in WorkflowBundle.AcceptedKinds)
        {
            var raw = $$$"""
                {"schema_version":"v3","kind":"{{{kind}}}","workflow":{"name":"x"},
                 "nodes":[],"edges":[],"dependencies":{}}
                """;
            Assert.True(WorkflowBundleReader.LooksLikeBundle(raw, "json"));
            Assert.Equal(kind, WorkflowBundleReader.Parse(raw).Kind);
        }

        var alien = """
            {"schema_version":"v3","kind":"itential.workflow","workflow":{"name":"x"},
             "nodes":[],"edges":[],"dependencies":{}}
            """;
        Assert.False(WorkflowBundleReader.LooksLikeBundle(alien, "json"));
    }

    [Fact]
    public void A_v2_bundle_is_read_with_its_requires_inferred()
    {
        // §2.3 — a v2 file declares nothing; the reader works out what it
        // would have declared from the nodes and snippets it can see, so the
        // rest of the importer has exactly one shape to reason about.
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v2","kind":"nashira.workflow_bundle",
             "workflow":{"name":"legacy"},
             "nodes":[{"id":"a","snippet_id":"{{{snippet}}}",
                       "config_overrides":{"text":"{{ steps.a.output.x | trim }}"}}],
             "edges":[{"id":"e","source":"a","target":"b","type":"conditional"}],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","name":"S","type":"ssh",
                                          "target_mode":"once","max_parallel":3}]}}
            """;

        var bundle = WorkflowBundleReader.Parse(raw);

        Assert.Equal(["ssh"], bundle.Requires!.SnippetTypes);
        Assert.Equal(["conditional_edges", "max_parallel", "template_filters"], bundle.Requires.Capabilities);
        Assert.Empty(bundle.Triggers);
    }

    [Fact]
    public void Unknown_members_are_ignored_rather_than_fatal()
    {
        // Forward compatibility (§1): a newer writer's extra members must not
        // turn a readable bundle into a parse error.
        var raw = """
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","what_is_this":42,
             "workflow":{"name":"x","invented_later":true},"nodes":[],"edges":[],
             "dependencies":{"snippets":[],"future_section":[1,2,3]}}
            """;

        Assert.Equal("x", WorkflowBundleReader.Parse(raw).Workflow.Name);
    }

    // ── §4 portable reference keys ────────────────────────────────────────

    [Fact]
    public async Task Export_writes_the_name_key_in_place_of_every_id_key()
    {
        using var db = TestDb.NewContext();
        var integration = Guid.NewGuid();
        var action = Guid.NewGuid();
        db.Integrations.Add(new Integration
        {
            IntegrationId = integration, Slug = "netbox", Name = "NetBox", Type = "generic_rest",
            BaseURL = "https://netbox.example.test", IsActive = true, Enabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = action, IntegrationId = integration, Name = "list_devices",
            Method = "GET", Path = "/api/dcim/devices/", Category = "dcim",
            Enabled = true, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var server = SeedMcpServer(db);
        var credential = SeedCredential(db);
        var repository = SeedRepository(db);
        var snippet = SeedSnippet(db, "Step", "step");

        var nodes = $$$"""
            [
              { "id": "call", "snippet_id": "{{{snippet}}}",
                "config_overrides": { "integration_id": "{{{integration}}}", "action_id": "{{{action}}}" } },
              { "id": "mcp", "snippet_id": "{{{snippet}}}",
                "config_overrides": { "mcp_server_id": "{{{server}}}" } },
              { "id": "shell", "snippet_id": "{{{snippet}}}",
                "config_overrides": { "credential_id": "{{{credential}}}" } },
              { "id": "clone", "snippet_id": "{{{snippet}}}",
                "config_overrides": { "repository_id": "{{{repository}}}" } }
            ]
            """;
        var wf = SeedWorkflow(db, "portable", nodes);

        var bundle = await BundleTest.Svc(db).BuildAsync(wf, default);
        var byId = bundle.Nodes.EnumerateArray()
            .ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("config_overrides"));

        // The canonical NAME key, carrying the portable identity: the
        // integration's SLUG, the action's name, the server/credential/
        // repository name (§4).
        Assert.Equal("netbox", byId["call"].GetProperty("integration").GetString());
        Assert.Equal("list_devices", byId["call"].GetProperty("action").GetString());
        Assert.Equal("netops-mcp", byId["mcp"].GetProperty("server").GetString());
        Assert.Equal("netops-ssh", byId["shell"].GetProperty("credential").GetString());
        Assert.Equal("configs", byId["clone"].GetProperty("repository").GetString());

        // CONTRACT CHANGE (§4): the local id key must NOT travel beside it.
        // A GUID is meaningful only on the instance that minted it, so
        // carrying one makes the node hash instance-specific and a bundle
        // that crossed products could never come back equal to itself.
        foreach (var (node, idKey) in new[]
                 {
                     ("call", "integration_id"), ("call", "action_id"), ("mcp", "mcp_server_id"),
                     ("shell", "credential_id"), ("clone", "repository_id"),
                 })
            Assert.False(byId[node].TryGetProperty(idKey, out _), $"{node}.{idKey} must not travel");
    }

    [Fact]
    public async Task An_id_the_instance_can_no_longer_name_is_kept_so_the_far_side_refuses()
    {
        // The one case §4's "MUST NOT emit the id" cannot be honoured: the
        // referenced row is gone HERE, so there is no portable name to write.
        // Dropping the key would strip the reference silently and produce a
        // bundle that imports clean and breaks at run time; keeping the id
        // makes the importer refuse with bundle_reference_untranslatable.
        using var db = TestDb.NewContext();
        var snippet = SeedSnippet(db, "Step", "step");
        var ghost = Guid.NewGuid();
        var nodes = $$$"""
            [{ "id": "shell", "snippet_id": "{{{snippet}}}",
               "config_overrides": { "credential_id": "{{{ghost}}}" } }]
            """;

        var bundle = await BundleTest.Svc(db).BuildAsync(SeedWorkflow(db, "orphan-ref", nodes), default);

        var overrides = bundle.Nodes.EnumerateArray().Single().GetProperty("config_overrides");
        Assert.Equal(ghost.ToString(), overrides.GetProperty("credential_id").GetString());
        Assert.False(overrides.TryGetProperty("credential", out _));
        Assert.Empty(bundle.Dependencies.Credentials);
    }

    [Fact]
    public async Task Import_translates_the_name_keys_to_local_ids_and_drops_them()
    {
        const string testName = nameof(Import_translates_the_name_keys_to_local_ids_and_drops_them);
        using var source = TestDb.NewContext();
        var srcServer = SeedMcpServer(source);
        var srcCredential = SeedCredential(source);
        var srcRepository = SeedRepository(source);
        var srcSnippet = SeedSnippet(source, "Step", "step");
        var nodes = $$$"""
            [{ "id": "n", "snippet_id": "{{{srcSnippet}}}",
               "config_overrides": { "mcp_server_id": "{{{srcServer}}}", "credential_id": "{{{srcCredential}}}",
                                     "repository_id": "{{{srcRepository}}}" } }]
            """;
        var bundle = await BundleTest.Svc(source).BuildAsync(SeedWorkflow(source, "portable", nodes), default);

        using var target = TestDb.NewContext("b-" + testName);
        var dstServer = SeedMcpServer(target);
        var dstCredential = SeedCredential(target);
        var dstRepository = SeedRepository(target);

        var resolution = await BundleTest.Svc(target).ResolveAsync(bundle, default);
        var overrides = resolution.Nodes.EnumerateArray().Single().GetProperty("config_overrides");

        Assert.Equal(dstServer.ToString(), overrides.GetProperty("mcp_server_id").GetString());
        Assert.Equal(dstCredential.ToString(), overrides.GetProperty("credential_id").GetString());
        Assert.Equal(dstRepository.ToString(), overrides.GetProperty("repository_id").GetString());
        // The stored graph keeps exactly one source of truth: this instance's
        // ids. A later export re-derives the names from what the node points
        // at NOW, never from a label that has since gone stale.
        Assert.False(overrides.TryGetProperty("server", out _));
        Assert.False(overrides.TryGetProperty("credential", out _));
        Assert.False(overrides.TryGetProperty("repository", out _));
    }

    [Fact]
    public async Task A_name_that_matches_nothing_is_refused_naming_the_node_and_the_key()
    {
        const string testName = nameof(A_name_that_matches_nothing_is_refused_naming_the_node_and_the_key);
        using var source = TestDb.NewContext();
        var srcCredential = SeedCredential(source, "vault-prod");
        var srcSnippet = SeedSnippet(source, "Step", "step");
        var nodes = $$$"""
            [{ "id": "login", "snippet_id": "{{{srcSnippet}}}",
               "config_overrides": { "credential_id": "{{{srcCredential}}}" } }]
            """;
        var bundle = await BundleTest.Svc(source).BuildAsync(SeedWorkflow(source, "w", nodes), default);

        // The credential is dropped from the dependencies table but the node
        // still names it: the name key alone has to be enough to refuse.
        bundle.Dependencies.Credentials.Clear();

        using var target = TestDb.NewContext("b-" + testName);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(target).ResolveAsync(bundle, default));

        Assert.Equal("bundle_reference_untranslatable", ex.Code);
        Assert.Contains("login", ex.Message);
        Assert.Contains("credential", ex.Message);
        Assert.Contains("vault-prod", ex.Message);
    }

    [Fact]
    public async Task Identity_only_sections_never_carry_secret_material()
    {
        // The pin that matters most. An MCP server, a credential and a git
        // repository are carried by IDENTITY: enough for the far side to bind
        // its own row, never enough to authenticate as this instance.
        using var db = TestDb.NewContext();
        var server = SeedMcpServer(db);
        var credential = SeedCredential(db);
        var repository = SeedRepository(db);
        var snippet = SeedSnippet(db, "Step", "step");
        var nodes = $$$"""
            [{ "id": "n", "snippet_id": "{{{snippet}}}",
               "config_overrides": { "mcp_server_id": "{{{server}}}", "credential_id": "{{{credential}}}",
                                     "repository_id": "{{{repository}}}" } }]
            """;
        var wf = SeedWorkflow(db, "leaky", nodes);
        SeedTrigger(db, wf, "on-push");

        var json = BundleTest.Serialize(await BundleTest.Svc(db).BuildAsync(wf, default));

        Assert.DoesNotContain("should-never-travel", json);
        foreach (var forbidden in new[]
                 {
                     "auth_config", "auth_config_encrypted", "encrypted_password", "encrypted_private_key",
                     "encrypted_secret", "auth_type", "tls_skip_verify", "auth_credential_id",
                     "target_devices", "last_run_at", "last_run_status", "next_run_at",
                 })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);

        // Not a secret, but the same pin: the local id keys never reach the
        // wire either (§4). They are meaningless off this instance and they
        // make the node hash instance-specific, which is what would quietly
        // turn the §8 round trip into a same-instance tautology.
        foreach (var localOnly in new[]
                 { "integration_id", "action_id", "mcp_server_id", "credential_id", "repository_id" })
            Assert.DoesNotContain(localOnly, json, StringComparison.OrdinalIgnoreCase);

        // What DOES travel: identity, and the URL a human needs to confirm
        // they are pointing at the same system.
        Assert.Contains("netops-mcp", json);
        Assert.Contains("git.example.test", json);
    }

    // ── §5.4 sub-workflows ────────────────────────────────────────────────

    [Fact]
    public async Task Export_flattens_every_reachable_subflow_once_and_merges_their_dependencies()
    {
        using var db = TestDb.NewContext();
        var leafSnippet = SeedSnippet(db, "Leaf", "leaf", type: "ssh", targetMode: "per_device");
        var midSnippet = SeedSnippet(db, "Mid", "mid");
        var rootSnippet = SeedSnippet(db, "Root", "root");

        var leaf = SeedWorkflow(db, "leaf-wf", $$$"""[{"id":"l","snippet_id":"{{{leafSnippet}}}"}]""");
        var mid = SeedWorkflow(db, "mid-wf", $$$"""
            [ {"id":"m","snippet_id":"{{{midSnippet}}}"},
              {"id":"call-leaf","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{leaf}}}"}} ]
            """);
        // The root reaches the leaf twice: directly and through the middle
        // workflow. It must still appear exactly once.
        var root = SeedWorkflow(db, "root-wf", $$$"""
            [ {"id":"r","snippet_id":"{{{rootSnippet}}}"},
              {"id":"call-mid","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{mid}}}"}},
              {"id":"call-leaf","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{leaf}}}"}} ]
            """);

        var bundle = await BundleTest.Svc(db).BuildAsync(root, default);

        Assert.Equal(["leaf-wf", "mid-wf"], bundle.Dependencies.Workflows.Select(w => w.Name).OrderBy(n => n));
        Assert.Single(bundle.Dependencies.Workflows, w => w.Id == leaf);
        // Their snippets are merged into the SAME dependencies section — a
        // sub-workflow's building blocks are the bundle's building blocks.
        Assert.Equal(
            ["Leaf", "Mid", "Root"],
            bundle.Dependencies.Snippets.Select(s => s.Name).OrderBy(n => n));
        // …and into the same requires block.
        Assert.Contains("ssh", bundle.Requires!.SnippetTypes);
        Assert.Contains(BundleCapabilities.Subflow, bundle.Requires.Capabilities);
    }

    [Fact]
    public async Task Export_refuses_a_subflow_cycle_rather_than_shipping_it()
    {
        using var db = TestDb.NewContext();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = a, Name = "a", Environment = "draft",
            Nodes = BundleTest.Json($$$"""[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{b}}}"}}]"""),
            Edges = BundleTest.Json("[]"), IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.Workflows.Add(new WorkflowModel
        {
            WorkflowId = b, Name = "b", Environment = "draft",
            Nodes = BundleTest.Json($$$"""[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{a}}}"}}]"""),
            Edges = BundleTest.Json("[]"), IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(db).BuildAsync(a, default));

        Assert.Equal("bundle_subflow_cycle", ex.Code);
    }

    [Fact]
    public void A_cycle_between_carried_sub_workflows_is_refused_on_read()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","workflow":{"name":"root"},
             "nodes":[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{a}}}"}}],
             "edges":[],
             "dependencies":{"workflows":[
               {"id":"{{{a}}}","name":"a","nodes":[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{b}}}"}}],"edges":[]},
               {"id":"{{{b}}}","name":"b","nodes":[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{a}}}"}}],"edges":[]}]}}
            """;

        var ex = Assert.Throws<ValidationException>(() => WorkflowBundleReader.Parse(raw));

        Assert.Equal("bundle_subflow_cycle", ex.Code);
    }

    [Fact]
    public void Sub_workflows_are_ordered_callees_first()
    {
        // The importer creates them in this order so a caller's
        // subflow_workflow_id can be remapped to a row that already exists.
        var leaf = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","workflow":{"name":"root"},
             "nodes":[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{mid}}}"}}],
             "edges":[],
             "dependencies":{"workflows":[
               {"id":"{{{mid}}}","name":"mid","nodes":[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{leaf}}}"}}],"edges":[]},
               {"id":"{{{leaf}}}","name":"leaf","nodes":[],"edges":[]}]}}
            """;

        var order = WorkflowBundleReader.SubflowOrder(WorkflowBundleReader.Parse(raw));

        Assert.Equal(["leaf", "mid"], order.Select(w => w.Name));
    }

    [Fact]
    public void Remap_rewrites_subflow_workflow_ids()
    {
        var source = Guid.NewGuid();
        var local = Guid.NewGuid();
        var nodes = BundleTest.Json(
            $$$"""[{"id":"n","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{source}}}","depth":2}}]""");

        var remapped = NodeReferences.Remap(nodes, new Dictionary<Guid, Guid> { [source] = local });
        var overrides = remapped.EnumerateArray().Single().GetProperty("config_overrides");

        Assert.Equal(local.ToString(), overrides.GetProperty("subflow_workflow_id").GetString());
        Assert.Equal(2, overrides.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task A_subflow_the_bundle_does_not_carry_and_this_instance_lacks_is_refused()
    {
        const string testName = nameof(A_subflow_the_bundle_does_not_carry_and_this_instance_lacks_is_refused);
        var orphan = Guid.NewGuid();
        using var db = TestDb.NewContext("b-" + testName);
        var snippet = SeedSnippet(db, "S", "s");
        var raw = $$$"""
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","workflow":{"name":"root"},
             "nodes":[{"id":"call","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{orphan}}}"}}],
             "edges":[],"dependencies":{}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(db).ResolveAsync(WorkflowBundleReader.Parse(raw), default));

        // `bundle_incomplete`, not `bundle_dependencies_missing`, and the change is the
        // instruction it gives. §5.4: a sub-workflow the bundle failed to carry is a fault of
        // the SENDER — no amount of local setup fixes it, so telling the receiver to go create
        // something was actively misleading. This test asserted the misleading code.
        Assert.Equal("bundle_incomplete", ex.Code);
        Assert.Contains(orphan.ToString(), ex.Message);
        Assert.Contains("Re-export it", ex.Message);
    }


    // ── §5.3 / §5.4: whose fault is it, and what does the message tell them to do ──

    // An integration a node names that NOBODY declared — not the bundle, not this instance.
    // §5.3 codes it `bundle_dependencies_missing`, and the distinction from
    // `bundle_reference_untranslatable` is the instruction it gives: "install this integration
    // here, with its own credentials" rather than "fix this node". It used to fall through to
    // the translation pass and send an operator to edit a node that was correct.
    [Fact]
    public async Task An_integration_nobody_declared_is_a_missing_dependency_not_a_bad_node()
    {
        using var db = TestDb.NewContext();
        var snippet = SeedSnippet(db, "Call", "integration_action");
        var raw = $$$"""
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","workflow":{"name":"root"},
             "nodes":[{"id":"call","snippet_id":"{{{snippet}}}",
                       "config_overrides":{"integration":"netbox","action":"list-devices"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"call","name":"Call",
                                          "type":"integration_action","target_mode":"once"}]}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(db).ResolveAsync(WorkflowBundleReader.Parse(raw), default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("netbox", ex.Message);
    }

    // The other side of the same line: an integration the bundle DID declare and this instance
    // lacks is already named by the dependency pass, with the richer description the
    // declaration carries. It must not be reported twice in one refusal.
    [Fact]
    public async Task A_declared_but_absent_integration_is_named_once()
    {
        using var db = TestDb.NewContext();
        var snippet = SeedSnippet(db, "Call", "integration_action");
        var raw = $$$"""
            {"schema_version":"v3","kind":"flow_weaver.workflow_bundle","workflow":{"name":"root"},
             "nodes":[{"id":"call","snippet_id":"{{{snippet}}}",
                       "config_overrides":{"integration":"netbox","action":"list-devices"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"call","name":"Call",
                                          "type":"integration_action","target_mode":"once"}],
                             "integrations":[{"id":"dddddddd-0000-0000-0000-00000000000d",
                                              "slug":"netbox","name":"netbox","type":"rest","actions":[]}]}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(db).ResolveAsync(WorkflowBundleReader.Parse(raw), default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);

        // ONE entry, not two. The dependency pass already named it, with the richer
        // description a declaration carries (slug, type, source base_url); the node-reference
        // pass must not add a second, thinner entry for the same fault. Two entries for one
        // problem reads as two problems to fix.
        //
        // Counted by ENTRY, not by occurrences of the word: the declaration's own description
        // legitimately says "netbox" twice, once as the name and once as the slug.
        Assert.Contains("slug 'netbox'", ex.Message);
        Assert.DoesNotContain("referenced by a node, not carried by the bundle", ex.Message);
    }

    // ── §2.3: a v2 bundle carries no triggers ─────────────────────────────

    // v2 predates the trigger section, so a `triggers` array in a v2 file is some other
    // product's shape under a name this one now uses. Importing it means creating a schedule
    // the author never asked THIS instance for.
    [Fact]
    public void A_v2_bundle_carries_no_triggers()
    {
        var raw = """
            {"schema_version":"v2","kind":"nashira.workflow_bundle","workflow":{"name":"audit"},
             "nodes":[],"edges":[],"dependencies":{},
             "triggers":[{"name":"nightly","type":"cron","cron_expression":"0 2 * * *"}]}
            """;

        var bundle = WorkflowBundleReader.Parse(raw);

        Assert.Empty(bundle.Triggers);
        // And the inferred `requires` must not claim the capability either — the drop happens
        // before inference precisely so this cannot say `triggers` for triggers it dropped.
        Assert.DoesNotContain("triggers", bundle.Requires!.Capabilities);
    }

    // ── contract-named keys are not "unknown" ─────────────────────────────

    // `changes` is the node-level change declaration. Before this, a node carrying it drew
    // the unknown-key note — which was wrong twice: it called a contract-named key a possible
    // typo, and it said "the handler ignores them" when the worker READS this one and fails
    // the step when a deferring type has no declaration anywhere.
    //
    // Found by importing an externally authored bundle into a live FlowWeaver, not by a unit
    // test: nothing here had a node that declared `changes` until the two products actually
    // exchanged one.
    [Fact]
    public void The_change_declaration_is_not_an_unknown_key()
    {
        var overrides = TestJson.Element("""{"commands":["show version"],"changes":false}""");

        var unknown = SnippetKeyCatalog.UnknownKeys("ssh", overrides);

        Assert.DoesNotContain("changes", unknown);
    }

    // Its sibling, and the precedent this follows.
    [Fact]
    public void The_idempotency_override_is_not_an_unknown_key()
    {
        var overrides = TestJson.Element("""{"commands":["show version"],"idempotency":"idempotent"}""");

        Assert.DoesNotContain("idempotency", SnippetKeyCatalog.UnknownKeys("ssh", overrides));
    }

    // The note still has to fire for a key that really is unrecognised, or removing two
    // names from it would have quietly disabled it.
    [Fact]
    public void A_genuinely_unknown_key_is_still_reported()
    {
        var overrides = TestJson.Element("""{"commands":["show version"],"banner":"hi"}""");

        Assert.Contains("banner", SnippetKeyCatalog.UnknownKeys("ssh", overrides));
    }

    // ── §6 triggers ───────────────────────────────────────────────────────

    [Fact]
    public async Task Export_carries_triggers_without_secret_targets_or_statistics()
    {
        using var db = TestDb.NewContext();
        var snippet = SeedSnippet(db, "S", "s");
        var wf = SeedWorkflow(db, "scheduled", $$$"""[{"id":"a","snippet_id":"{{{snippet}}}"}]""");
        SeedTrigger(db, wf, "nightly", type: "cron", route: null);
        SeedTrigger(db, wf, "on-push");

        var bundle = await BundleTest.Svc(db).BuildAsync(wf, default);

        Assert.Equal(2, bundle.Triggers.Count);
        var cron = bundle.Triggers.Single(t => t.Name == "nightly");
        Assert.Equal("0 3 * * *", cron.CronExpression);
        Assert.Equal("America/Bogota", cron.Timezone);
        Assert.Contains(BundleCapabilities.Triggers, bundle.Requires!.Capabilities);
    }

    [Fact]
    public async Task Import_creates_triggers_disabled_without_targets_and_keeps_a_free_route()
    {
        const string testName = nameof(Import_creates_triggers_disabled_without_targets_and_keeps_a_free_route);
        using var source = TestDb.NewContext();
        var snippet = SeedSnippet(source, "S", "s");
        var wf = SeedWorkflow(source, "scheduled", $$$"""[{"id":"a","snippet_id":"{{{snippet}}}"}]""");
        SeedTrigger(source, wf, "on-push", route: "abc123");
        var bundle = await BundleTest.Svc(source).BuildAsync(wf, default);

        using var target = TestDb.NewContext("b-" + testName);
        var resolution = await BundleTest.Svc(target).ResolveAsync(bundle, default);

        var dto = Assert.Single(resolution.Triggers);
        Assert.False(dto.Enabled);
        Assert.Empty(dto.TargetDevices!);
        Assert.False(dto.AllowUnsigned);
        // Free here, so the source's route is kept: an existing webhook
        // producer keeps working after the workflow moves.
        Assert.Equal("abc123", dto.Route);
        Assert.Contains(resolution.Notes, n => n.Contains("on-push") && n.Contains("disabled"));
    }

    [Fact]
    public async Task Import_regenerates_a_route_already_taken_here_and_says_so()
    {
        const string testName = nameof(Import_regenerates_a_route_already_taken_here_and_says_so);
        using var source = TestDb.NewContext();
        var snippet = SeedSnippet(source, "S", "s");
        var wf = SeedWorkflow(source, "scheduled", $$$"""[{"id":"a","snippet_id":"{{{snippet}}}"}]""");
        SeedTrigger(source, wf, "on-push", route: "abc123");
        var bundle = await BundleTest.Svc(source).BuildAsync(wf, default);

        using var target = TestDb.NewContext("b-" + testName);
        var other = SeedWorkflow(target, "other", "[]");
        SeedTrigger(target, other, "existing", route: "abc123");

        var resolution = await BundleTest.Svc(target).ResolveAsync(bundle, default);

        var dto = Assert.Single(resolution.Triggers);
        Assert.NotEqual("abc123", dto.Route);
        Assert.Contains(resolution.Notes, n => n.Contains("route 'abc123'") && n.Contains("regenerated"));
    }

    // ── §3 retry policy (execution/SPEC.md §3) ────────────────────────────

    [Fact]
    public void The_legacy_retry_shape_is_translated_on_read()
    {
        var legacy = BundleTest.Json("""{"max_attempts":3,"delay_seconds":10,"backoff":"linear"}""");

        var result = RetryPolicyTranslation.ToCanonical(legacy);

        Assert.True(result.Translated);
        // max_attempts counts the FIRST try; max_retries does not. Reading the
        // number across unchanged would grant one extra attempt on every
        // imported step.
        Assert.Equal(2, result.Policy.GetProperty("max_retries").GetInt32());
        Assert.Equal(10, result.Policy.GetProperty("initial_delay_seconds").GetDouble());
        Assert.Equal("linear", result.Policy.GetProperty("backoff").GetString());
        Assert.Equal(30, result.Policy.GetProperty("max_delay_seconds").GetDouble());
    }

    [Fact]
    public void A_canonical_retry_policy_is_left_exactly_as_it_is()
    {
        var canonical = BundleTest.Json(
            """{"max_retries":2,"initial_delay_seconds":5,"backoff":"exponential","max_delay_seconds":300}""");

        var result = RetryPolicyTranslation.ToCanonical(canonical);

        Assert.False(result.Translated);
        Assert.Equal(300, result.Policy.GetProperty("max_delay_seconds").GetDouble());
    }

    [Fact]
    public void A_missing_or_malformed_retry_policy_means_no_retry()
    {
        Assert.False(RetryPolicyTranslation.ToCanonical(null).Translated);
        Assert.Equal(JsonValueKind.Undefined, RetryPolicyTranslation.ToCanonical(null).Policy.ValueKind);
        Assert.Equal(
            JsonValueKind.Undefined,
            RetryPolicyTranslation.ToCanonical(BundleTest.Json("\"nonsense\"")).Policy.ValueKind);
    }

    [Fact]
    public async Task A_legacy_retry_policy_on_a_carried_snippet_is_translated_and_noted()
    {
        const string testName = nameof(A_legacy_retry_policy_on_a_carried_snippet_is_translated_and_noted);
        using var target = TestDb.NewContext("b-" + testName);
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"nashira.workflow_bundle","workflow":{"name":"retried"},
             "nodes":[{"id":"a","snippet_id":"{{{snippet}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"retried","name":"Retried","type":"ssh",
               "target_mode":"once","max_parallel":1,"timeout_seconds":60,
               "retry_policy":{"max_attempts":4,"delay_seconds":2,"backoff":"fixed"}}]}}
            """;

        var resolution = await BundleTest.Svc(target).ResolveAsync(WorkflowBundleReader.Parse(raw), default);

        var created = Assert.Single(resolution.SnippetsToCreate);
        Assert.Equal(3, created.RetryPolicy.GetProperty("max_retries").GetInt32());
        Assert.Contains(resolution.Notes, n => n.Contains("retry_policy") && n.Contains("legacy"));
    }

    // ── §8 round trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task Export_import_export_leaves_the_canonical_graph_and_requires_unchanged()
    {
        const string testName = nameof(Export_import_export_leaves_the_canonical_graph_and_requires_unchanged);
        using var source = TestDb.NewContext();
        var server = SeedMcpServer(source);
        var credential = SeedCredential(source);
        var per = SeedSnippet(source, "Show", "show", type: "ssh", targetMode: "per_device", maxParallel: 2);
        var plain = SeedSnippet(source, "Step", "step");
        var child = SeedWorkflow(source, "child-wf", $$$"""[{"id":"c","snippet_id":"{{{plain}}}"}]""");
        var nodes = $$$"""
            [ {"id":"show","snippet_id":"{{{per}}}","config_overrides":{"credential_id":"{{{credential}}}"}},
              {"id":"read","snippet_id":"{{{per}}}",
               "config_overrides":{"mcp_server_id":"{{{server}}}","text":"{{ steps.show.output.stdout | trim }}"}},
              {"id":"sub","snippet_id":"subflow","config_overrides":{"subflow_workflow_id":"{{{child}}}"}} ]
            """;
        var wf = SeedWorkflow(source, "round-trip", nodes,
            edges: """[{"id":"e","source":"show","target":"read","type":"conditional","condition":"{{ steps.show.output.ok }}"}]""");

        var first = await BundleTest.Svc(source).BuildAsync(wf, default);

        // Instance B: same systems, its own ids.
        using var target = TestDb.NewContext("b-" + testName);
        SeedMcpServer(target);
        SeedCredential(target);
        var resolution = await BundleTest.Svc(target).ResolveAsync(first, default);

        // Stand in for the writer: the snippets the bundle carried, created
        // here, and the graphs as they would be stored.
        target.Snippets.AddRange(resolution.SnippetsToCreate);
        target.SaveChanges();
        var idMap = new Dictionary<Guid, Guid>(resolution.IdMap);
        var storedChild = SeedWorkflow(
            target, "child-wf",
            JsonSerializer.Serialize(NodeReferences.Remap(resolution.Subflows.Single().Nodes, idMap)));
        idMap[child] = storedChild;
        var stored = SeedWorkflow(
            target, "round-trip",
            JsonSerializer.Serialize(NodeReferences.Remap(resolution.Nodes, idMap)),
            edges: JsonSerializer.Serialize(first.Edges));

        var second = await BundleTest.Svc(target).BuildAsync(stored, default);

        // §8: the hash compared here is the one over the BUNDLE's own
        // nodes+edges — the wire form — not over the imported workflow row.
        // The stored row carries this instance's ids; the wire form carries
        // portable identities only, which is exactly why §4 forbids ids on it.
        // `snippet_id` and `subflow_workflow_id` are the sanctioned exception:
        // they are remapped by design, so they are normalized rather than
        // compared. WireForm is asserted first so a failure is readable; the
        // hash is the assertion the contract actually makes.
        Assert.Equal(
            BundleTest.WireForm(first.Nodes, first.Edges),
            BundleTest.WireForm(second.Nodes, second.Edges));
        Assert.Equal(
            BundleTest.WireHash(first.Nodes, first.Edges),
            BundleTest.WireHash(second.Nodes, second.Edges));
        Assert.Equal(first.Requires!.Capabilities, second.Requires!.Capabilities);
        Assert.Equal(first.Requires.SnippetTypes, second.Requires.SnippetTypes);
        Assert.Equal(
            first.Dependencies.Workflows.Select(w => w.Name),
            second.Dependencies.Workflows.Select(w => w.Name));
    }

    [Fact]
    public async Task Re_importing_a_bundle_into_the_instance_that_wrote_it_is_byte_exact()
    {
        // The sharper half of §8: within ONE instance nothing has to be
        // translated, so the canonical hash of nodes+edges must come back
        // identical. This is what makes "export it, keep it in git, restore it"
        // a safe operation rather than a lossy one.
        using var db = TestDb.NewContext();
        var server = SeedMcpServer(db);
        var credential = SeedCredential(db);
        var per = SeedSnippet(db, "Show", "show", type: "ssh", targetMode: "per_device", maxParallel: 2);
        var nodes = $$$"""
            [ {"id":"show","snippet_id":"{{{per}}}","config_overrides":{"credential_id":"{{{credential}}}"}},
              {"id":"read","snippet_id":"{{{per}}}",
               "config_overrides":{"mcp_server_id":"{{{server}}}","text":"{{ steps.show.output.stdout | trim }}"}} ]
            """;
        var wf = SeedWorkflow(db, "same-instance", nodes);

        var first = await BundleTest.Svc(db).BuildAsync(wf, default);
        var resolution = await BundleTest.Svc(db).ResolveAsync(first, default);
        var stored = SeedWorkflow(
            db, "same-instance-again",
            JsonSerializer.Serialize(NodeReferences.Remap(resolution.Nodes, resolution.IdMap)));

        var second = await BundleTest.Svc(db).BuildAsync(stored, default);

        Assert.Empty(resolution.SnippetsToCreate);
        Assert.Equal(
            CanonicalJson.Sha256(first.Nodes, first.Edges),
            CanonicalJson.Sha256(second.Nodes, second.Edges));
        Assert.Equal(first.Requires!.Capabilities, second.Requires!.Capabilities);
    }

    // ── §2.4 secrets ──────────────────────────────────────────────────────

    [Fact]
    public async Task Secret_references_are_kept_verbatim_listed_and_noted()
    {
        const string testName = nameof(Secret_references_are_kept_verbatim_listed_and_noted);
        using var target = TestDb.NewContext("b-" + testName);
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"nashira.workflow_bundle","workflow":{"name":"secretive"},
             "nodes":[{"id":"show","snippet_id":"{{{snippet}}}",
                       "config_overrides":{"password":"${secret:store:netops-ssh:password}"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"show","name":"Show","type":"ping",
               "target_mode":"once","max_parallel":1,"timeout_seconds":30}]},
             "requires":{"snippet_types":["ping"],"capabilities":[],
               "secrets":[{"ref":"${secret:store:netops-ssh:password}","used_by":["show"]}]}}
            """;

        var bundle = WorkflowBundleReader.Parse(raw);
        var resolution = await BundleTest.Svc(target).ResolveAsync(bundle, default);
        var overrides = resolution.Nodes.EnumerateArray().Single().GetProperty("config_overrides");

        // The marker is not a template and not a value: it travels through
        // untouched, and the operator is told to create the secret.
        Assert.Equal("${secret:store:netops-ssh:password}", overrides.GetProperty("password").GetString());
        Assert.Contains(resolution.Notes, n => n.Contains("${secret:store:netops-ssh:password}"));
    }

    [Fact]
    public void A_plain_inline_ssh_password_is_untranslatable_but_a_secret_reference_is_not()
    {
        var sshSnippet = Guid.NewGuid();
        var ids = new HashSet<Guid> { sshSnippet };

        var plain = BundleTest.Json(
            $$$"""[{"id":"login","snippet_id":"{{{sshSnippet}}}","config_overrides":{"password":"hunter2"}}]""");
        Assert.Equal(
            ("login", "password"),
            Assert.Single(NodeReferences.FindPlainInlineCredentials(plain, ids)));

        var referenced = BundleTest.Json(
            $$$"""[{"id":"login","snippet_id":"{{{sshSnippet}}}","config_overrides":{"password":"${secret:store:x:password}"}}]""");
        Assert.Empty(NodeReferences.FindPlainInlineCredentials(referenced, ids));
    }

    // ── §2.1 snippet types ────────────────────────────────────────────────

    [Fact]
    public async Task A_snippet_type_with_no_handler_here_is_refused_naming_the_type_and_its_users()
    {
        const string testName = nameof(A_snippet_type_with_no_handler_here_is_refused_naming_the_type_and_its_users);
        using var target = TestDb.NewContext("b-" + testName);
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"nashira.workflow_bundle","workflow":{"name":"mailbox"},
             "nodes":[{"id":"read","snippet_id":"{{{snippet}}}"}],"edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"inbox","name":"Read Inbox",
               "type":"email_mailbox","target_mode":"once","max_parallel":1,"timeout_seconds":30}]},
             "requires":{"snippet_types":["email_mailbox"],"capabilities":[],"secrets":[]}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => BundleTest.Svc(target).ResolveAsync(WorkflowBundleReader.Parse(raw), default));

        Assert.Equal("bundle_dependencies_missing", ex.Code);
        Assert.Contains("email_mailbox", ex.Message);
        Assert.Contains("Read Inbox", ex.Message);
    }

    [Fact]
    public async Task A_key_no_alias_covers_is_noted_per_node_not_silently_ignored()
    {
        const string testName = nameof(A_key_no_alias_covers_is_noted_per_node_not_silently_ignored);
        using var target = TestDb.NewContext("b-" + testName);
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"nashira.workflow_bundle","workflow":{"name":"typo"},
             "nodes":[{"id":"probe","snippet_id":"{{{snippet}}}","config_overrides":{"host":"10.0.0.1","tiemout_ms":500}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"probe","name":"Probe","type":"ping",
               "target_mode":"once","max_parallel":1,"timeout_seconds":30}]}}
            """;

        var resolution = await BundleTest.Svc(target).ResolveAsync(WorkflowBundleReader.Parse(raw), default);

        Assert.Contains(resolution.Notes, n => n.Contains("probe") && n.Contains("tiemout_ms"));
    }

    // Refused, not noted. The catalogued `rest_call` form IS the `rest_catalog`
    // capability, and this product does not implement it — its equivalent is an
    // `integration_action` node — so bundle/SPEC.md §2.2 makes it a refusal.
    //
    // This test used to assert a note and a successful resolve, which is the exact
    // failure the capability block exists to remove: a note describes a degradation,
    // and there is no degraded way to make a call this instance cannot make. The
    // workflow imported clean and the step failed on its first run.
    //
    // The bundle below declares no `requires` block at all — what a v2 exporter or a
    // hand-written file looks like — so this also pins that the refusal survives §2.3
    // inference and does not depend on the sender being honest about it.
    [Fact]
    public async Task A_catalogued_rest_call_node_is_refused_at_import()
    {
        const string testName = nameof(A_catalogued_rest_call_node_is_refused_at_import);
        using var target = TestDb.NewContext("b-" + testName);
        var snippet = Guid.NewGuid();
        var raw = $$$"""
            {"schema_version":"v3","kind":"nashira.workflow_bundle","workflow":{"name":"catalogued"},
             "nodes":[{"id":"call","snippet_id":"{{{snippet}}}",
                       "config_overrides":{"source":"netbox","operation_id":"dcim_devices_list"}}],
             "edges":[],
             "dependencies":{"snippets":[{"id":"{{{snippet}}}","slug":"call","name":"Call","type":"rest_call",
               "target_mode":"once","max_parallel":1,"timeout_seconds":30}]}}
            """;

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            BundleTest.Svc(target).ResolveAsync(WorkflowBundleReader.Parse(raw), default));

        Assert.Equal("bundle_capability_unsupported", ex.Code);
        Assert.Contains(BundleCapabilities.RestCatalog, ex.Message, StringComparison.Ordinal);
    }
}
