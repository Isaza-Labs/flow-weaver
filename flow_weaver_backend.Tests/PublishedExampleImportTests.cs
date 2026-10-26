using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Workflow;
using flow_weaver_backend.Services.WorkflowTrigger;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Every bundle under examples/ is something a new user downloads and imports on an instance
// that has nothing else configured. These import each one through the real path
// (WorkflowBundleReader.Parse -> WorkflowBundleImporter.ImportAsync) into an EMPTY database:
// no integrations, credentials, MCP servers or repositories. An example that needs anything
// the bundle does not carry is refused here, which is the hidden-reference failure a reader
// would otherwise hit first.
public class PublishedExampleImportTests
{
    private static string ExamplesRoot => Path.Combine(AppContext.BaseDirectory, "examples");

    private static IEnumerable<string> BundlePaths() =>
        Directory.GetFiles(ExamplesRoot, "*.bundle.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ExamplesRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    public static TheoryData<string> Bundles() => new(BundlePaths());

    // Guards the theory below against passing vacuously when the files stop being copied.
    [Fact]
    public void The_first_workflow_example_is_published()
    {
        Assert.Contains("first-workflow/leaf-reachability-check.bundle.json", BundlePaths());
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public async Task Imports_into_an_empty_instance_as_a_draft(string relative)
    {
        var raw = await File.ReadAllTextAsync(Path.Combine(ExamplesRoot, relative));
        var bundle = WorkflowBundleReader.Parse(raw);

        // An example must not need a secret, an integration or any other instance-specific
        // object to import.
        Assert.Empty(bundle.Requires?.Secrets ?? []);
        Assert.Empty(bundle.Dependencies.Integrations);
        Assert.Empty(bundle.Dependencies.Credentials);
        Assert.Empty(bundle.Dependencies.McpServers);
        Assert.Empty(bundle.Dependencies.Repositories);

        await using var db = TestDb.NewContext();
        var bundles = BundleTest.Svc(db);
        var workflows = new WorkflowService(
            new WorkflowRepository(db),
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
            new WorkflowTriggerRepository(db),
            new WorkflowRepository(db),
            new FakeUser(),
            new AllowAllEffectivePermissions(),
            new FakeCrypto(),
            new FakeAudit(),
            NullLogger<WorkflowTriggerService>.Instance);
        var importer = new WorkflowBundleImporter(
            bundles, workflows, triggers, new SnippetRepository(db),
            new UnitOfWork(db), NullLogger<WorkflowBundleImporter>.Instance);

        var imported = await importer.ImportAsync(bundle, default);

        Assert.Equal(bundle.Workflow.Name, imported.Name);
        Assert.Equal("draft", imported.Environment);
        Assert.Empty(imported.ImportNotes ?? []);
    }

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
