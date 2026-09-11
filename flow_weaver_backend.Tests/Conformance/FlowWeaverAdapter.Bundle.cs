using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Workflow;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;
using CredentialModel = flow_weaver_backend.Models.Credential;
using GitRepositoryModel = flow_weaver_backend.Models.GitRepository;
using McpServerModel = flow_weaver_backend.Models.McpServer;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests.Conformance;

// Family `bundle` (conformance/bundle/SPEC.md §4–§8).
//
//   { bundle, local?, round_trip? } -> { outcome, codes, schema_hash, notes_empty, created,
//                                        workflow, triggers, requires, node_override_keys,
//                                        schema_hash_unchanged, exported_node_overrides,
//                                        exported_node_override_keys }
//
// `local` is what the RECEIVING instance already holds, and it has to be part of the vector:
// §5 refuses a bundle whose integrations, credentials, repositories or handler types are
// missing here, so "refused" and "imported" are answers about a PAIR (bundle, instance). A
// vector naming only the bundle would be asserting half a question.
//
//   local.snippet_types   which handlers this instance ships
//   local.integrations    [{ slug, name, type, actions: [{ name }] }]
//   local.credentials     [{ name }]
//   local.repositories    [{ name }]
//   local.mcp_servers     [{ name }]
//
// ── What is REAL here ───────────────────────────────────────────────────────
//
// The whole import path: `WorkflowBundleReader.Parse` → `WorkflowBundleService.ResolveAsync`
// → `WorkflowBundleImporter.ImportAsync`, against an in-memory database, with the REAL
// `WorkflowService` and `WorkflowTriggerService` behind the importer.
//
// That last part is why this adapter carries a fixture instead of stopping at `ResolveAsync`
// the way this repository's own bundle tests do. Several vectors assert where an import
// LANDS — `environment: draft`, `version: 1`, no simulation — and what a created trigger looks
// like: disabled, with a freshly generated secret. None of that exists in a resolution; it is
// produced by the services that persist the thing. Answering it from the resolution would be
// the harness stating what the product would presumably do, which is the one thing an adapter
// must never do.
//
// ── schema_hash ─────────────────────────────────────────────────────────────
//
// §8's hash is over the BUNDLE's own wire nodes+edges (`BundleTest.WireHash`), never over the
// stored row. The two are deliberately different things: the row carries whatever local
// vocabulary this product runs on, while the wire form carries portable identities only.
// Comparing stored rows would assert that two products store workflows identically, which is
// neither the contract nor true.
public sealed partial class FlowWeaverAdapter
{
    private static JsonElement? Bundle(JsonElement input)
    {
        var raw = PropOrNull(input, "bundle") ?? input;
        var local = PropOrNull(input, "local");

        using var kit = new BundleHarness(local);

        WorkflowBundle bundle;
        try
        {
            bundle = WorkflowBundleReader.Parse(raw.GetRawText());
        }
        catch (ValidationException ex)
        {
            return Refused(ex);
        }

        // Taken BEFORE the import, off the bundle as it arrived. Hashing after would hash
        // whatever the import did to it.
        var wireHash = BundleTest.WireHash(bundle.Nodes, bundle.Edges);

        WorkflowResponse imported;
        try
        {
            imported = kit.Importer.ImportAsync(bundle, default).GetAwaiter().GetResult();
        }
        catch (ValidationException ex)
        {
            return Refused(ex);
        }

        var result = new Dictionary<string, object?>
        {
            ["outcome"] = "imported",
            ["codes"] = Array.Empty<string>(),
            ["schema_hash"] = wireHash,
            ["notes"] = imported.ImportNotes,
            // §7: "Silence means nothing was degraded." That is the assertion a vector can
            // actually make about notes — the WORDING of each note is a product's own, the
            // presence or absence of any is the contract's.
            ["notes_empty"] = imported.ImportNotes.Count == 0,
            ["created"] = new
            {
                snippets = imported.CreatedSnippets.Count,
                // Sub-workflows only. The workflow the bundle is ABOUT is not "created
                // extra" — every successful import produces exactly one of those, so
                // counting it would make the number say nothing.
                workflows = imported.CreatedWorkflows.Count,
                triggers = imported.CreatedTriggers.Count,
            },
            ["workflow"] = new
            {
                environment = imported.Environment,
                version = imported.Version,
                schema_version = imported.SchemaVersion,
                // Off the stored ROW: the response DTO does not carry it, and the vector
                // is asking what the import LANDED as, not what the API happens to return.
                has_simulation = kit.HasSimulation(imported.WorkflowId),
            },
            ["triggers"] = kit.Triggers(imported.WorkflowId),
            ["requires"] = Requires(bundle.Requires),
            ["node_overrides"] = kit.Overrides(imported.WorkflowId),
            // The stored payloads' EXACT key set per node. This is what proves a legacy id
            // key was DROPPED rather than left lying beside its canonical name — an absence
            // that no "these fields are present" comparison can see.
            ["node_override_keys"] = kit.OverrideKeys(imported.WorkflowId),
        };

        if (PropOrNull(input, "round_trip") is { ValueKind: JsonValueKind.True })
        {
            var reexported = kit.Bundles.BuildAsync(imported.WorkflowId, default).GetAwaiter().GetResult();
            result["schema_hash_unchanged"] =
                BundleTest.WireHash(reexported.Nodes, reexported.Edges) == wireHash;
            result["requires"] = Requires(reexported.Requires);
            // What went back ON THE WIRE, which is the only place §4 constrains the value:
            // what a product stores is its business; what it puts on the wire is the contract.
            var wire = WireOverrides(reexported.Nodes);
            result["exported_node_overrides"] = wire;
            result["exported_node_override_keys"] = wire.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.EnumerateObject().Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList());
        }

        return JsonSerializer.SerializeToElement(result);
    }

    // Every refusal code the failure names.
    //
    // §7 says a refusal "names every missing dependency, unsupported capability or
    // untranslatable key AT ONCE", so this reads the message for further codes as well as
    // taking the exception's own. Today this product raises one at a time and every vector
    // asserts a single code, so the set has one member — but a reader looking only at
    // `ex.Code` would let the multi-fault case rot unnoticed the day it matters.
    private static JsonElement Refused(ValidationException ex)
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ex.Code)) codes.Add(ex.Code);
        foreach (var known in KnownRefusalCodes)
            if (ex.Message.Contains($"[{known}]", StringComparison.Ordinal))
                codes.Add(known);

        return JsonSerializer.SerializeToElement(new
        {
            outcome = "refused",
            codes = codes.ToList(),
            // Nothing was written. A refusal that had already created a snippet would be a
            // half-import, which is the thing §5 refuses in order to prevent.
            created = new { snippets = 0, workflows = 0, triggers = 0 },
        });
    }

    private static readonly string[] KnownRefusalCodes =
    [
        "bundle_version_unsupported", "bundle_capability_unsupported", "bundle_incomplete",
        "bundle_dependencies_missing", "bundle_reference_untranslatable", "bundle_subflow_cycle",
    ];

    private static object Requires(BundleRequires r) => new
    {
        snippet_types = r.SnippetTypes,
        capabilities = r.Capabilities,
        secrets = r.Secrets.Select(s => new { @ref = s.Ref, used_by = s.UsedBy }).ToList(),
    };

    private static Dictionary<string, JsonElement> WireOverrides(JsonElement nodes) =>
        nodes.ValueKind != JsonValueKind.Array
            ? []
            : nodes.EnumerateArray()
                .Where(n => n.TryGetProperty("config_overrides", out var c)
                            && c.ValueKind == JsonValueKind.Object)
                .ToDictionary(
                    n => n.GetProperty("id").GetString()!,
                    n => n.GetProperty("config_overrides").Clone());

    // The receiving instance: an in-memory database holding whatever `local` says it holds,
    // and the real services wired over it.
    private sealed class BundleHarness : IDisposable
    {
        private readonly AppDbContext _db;

        public IWorkflowBundleService Bundles { get; }
        public IWorkflowBundleImporter Importer { get; }

        public BundleHarness(JsonElement? local)
        {
            _db = TestDb.NewContext();
            Seed(local);

            var handlerTypes = StringsOf(local, "snippet_types");
            Bundles = BundleTest.Svc(_db, handlerTypes.Length > 0 ? handlerTypes : ["__none__"]);

            var workflows = new WorkflowService(
                new WorkflowRepository(_db),
                new FakeUser(),
                new FakeSchemaValidator(),
                new FakeReferenceValidator(),
                new FakeVendorCommandValidator(),
                new FakeAudit(),
                new FakeTrace(),
                new FakePolicyEvaluator(),
                new AllowAll(),
                new FakeAppSettings(),
                NullLogger<WorkflowService>.Instance);

            var triggers = new WorkflowTriggerService(
                new WorkflowTriggerRepository(_db),
                new WorkflowRepository(_db),
                new FakeUser(),
                new FakeCrypto(),
                new FakeAudit(),
                NullLogger<WorkflowTriggerService>.Instance);

            Importer = new WorkflowBundleImporter(
                Bundles, workflows, triggers, new SnippetRepository(_db),
                new UnitOfWork(_db), NullLogger<WorkflowBundleImporter>.Instance);
        }

        // What the receiving instance already has. A bundle is refused or imported against
        // THIS, not against nothing.
        private void Seed(JsonElement? local)
        {
            foreach (var i in ObjectsOf(local, "integrations"))
            {
                var integration = new IntegrationModel
                {
                    IntegrationId = Guid.NewGuid(),
                    Name = StrOf(i, "name") ?? StrOf(i, "slug") ?? "integration",
                    Slug = StrOf(i, "slug"),
                    Type = StrOf(i, "type") ?? "rest",
                    BaseURL = "https://example.invalid",
                    IsActive = true,
                };
                _db.Integrations.Add(integration);

                foreach (var a in ObjectsOf(i, "actions"))
                    _db.IntegrationActions.Add(new IntegrationActionModel
                    {
                        IntegrationActionId = Guid.NewGuid(),
                        IntegrationId = integration.IntegrationId,
                        Name = StrOf(a, "name") ?? "action",
                        Method = StrOf(a, "method") ?? "GET",
                        Path = "/",
                        IsActive = true,
                    });
            }

            foreach (var c in ObjectsOf(local, "credentials"))
                _db.Credentials.Add(new CredentialModel
                {
                    CredentialId = Guid.NewGuid(),
                    Name = StrOf(c, "name") ?? "credential",
                    Type = "ssh",
                    Username = "u",
                    IsActive = true,
                });

            foreach (var r in ObjectsOf(local, "repositories"))
                _db.GitRepositories.Add(new GitRepositoryModel
                {
                    GitRepositoryId = Guid.NewGuid(),
                    Name = StrOf(r, "name") ?? "repo",
                    Url = "https://example.invalid/repo.git",
                    IsActive = true,
                });

            foreach (var m in ObjectsOf(local, "mcp_servers"))
                _db.McpServers.Add(new McpServerModel
                {
                    McpServerId = Guid.NewGuid(),
                    Name = StrOf(m, "name") ?? "mcp",
                    Url = "https://example.invalid/mcp",
                    IsActive = true,
                });

            _db.SaveChanges();
        }

        // Keyed by NAME rather than listed, so a vector can assert the fields §6 fixes for one
        // trigger without also pinning the ones it does not — a cron trigger has no HMAC
        // secret to be fresh; a webhook does.
        public Dictionary<string, object> Triggers(Guid workflowId) =>
            _db.WorkflowTriggers
                .Where(t => t.WorkflowId == workflowId)
                .AsEnumerable()
                .ToDictionary(t => t.Name, t => (object)new
                {
                    name = t.Name,
                    type = t.Type,
                    enabled = t.Enabled,
                    has_secret = t.EncryptedSecret is { Length: > 0 },
                    // As JSON text, which is the shape the contract compares — a trigger
                    // imported without targets must be visibly EMPTY rather than absent.
                    targets = JsonSerializer.Serialize(t.TargetDevices),
                });

        public bool HasSimulation(Guid workflowId) =>
            _db.Workflows.Where(w => w.WorkflowId == workflowId)
                .AsEnumerable().Any(w => w.LastSimulationId != null);

        private JsonElement Nodes(Guid workflowId) =>
            _db.Workflows.Where(w => w.WorkflowId == workflowId)
                .AsEnumerable().Select(w => w.Nodes).Single();

        public Dictionary<string, JsonElement> Overrides(Guid workflowId)
        {
            var nodes = Nodes(workflowId);
            return nodes.ValueKind != JsonValueKind.Array
                ? []
                : nodes.EnumerateArray()
                    .Where(n => n.TryGetProperty("config_overrides", out var c)
                                && c.ValueKind == JsonValueKind.Object)
                    .ToDictionary(
                        n => n.GetProperty("id").GetString()!,
                        n => n.GetProperty("config_overrides").Clone());
        }

        public Dictionary<string, List<string>> OverrideKeys(Guid workflowId) =>
            Overrides(workflowId).ToDictionary(
                kv => kv.Key,
                kv => kv.Value.EnumerateObject().Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList());

        public void Dispose() => _db.Dispose();

        private sealed class AllowAll : flow_weaver_backend.Services.Permission.IResourcePermissionService
        {
            public Task<bool> HasAtLeastAsync(string t, Guid id, string role, CancellationToken ct)
                => Task.FromResult(true);
            public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(string t, Guid id, CancellationToken ct)
                => throw new NotSupportedException();
            public Task<ResourcePermissionResponse> GrantAsync(
                string t, Guid id, GrantResourcePermissionRequest dto, CancellationToken ct)
                => throw new NotSupportedException();
            public Task RevokeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }
    }

    private static string[] StringsOf(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).ToArray()
            : [];

    private static IEnumerable<JsonElement> ObjectsOf(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : [];
}
