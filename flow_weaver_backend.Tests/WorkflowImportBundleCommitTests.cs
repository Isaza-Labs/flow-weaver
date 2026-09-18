using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Import;
using flow_weaver_backend.Services.Import.Models;
using flow_weaver_backend.Services.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Committing a bundle goes to the bundle importer instead of applying the
// wizard's resolutions, and it must still answer in the wizard's shape.
//
// It did not, once. It returned the WorkflowResponse the importer produces —
// which carries `warnings: null` — and the wizard does:
//
//     committedWorkflowId = res.workflow_id;
//     step = 'committed';
//     for (const w of res.warnings) toast.warning(...)
//
// So the import succeeded, the workflow was created, it ran, and the screen said
// "Commit failed". The QA pass that verified the commit missed it because it read
// `workflow_id` and nothing else. These assert the whole contract.
public class WorkflowImportBundleCommitTests
{
    private const string BundleJson = """
        {
          "schema_version": "v3",
          "kind": "flow_weaver.workflow_bundle",
          "workflow": { "name": "imported", "environment": "draft" },
          "nodes": [], "edges": [],
          "dependencies": { "snippets": [] }
        }
        """;

    private sealed class StubImporter : IWorkflowBundleImporter
    {
        public WorkflowResponse Response { get; set; } = new();
        public int Calls { get; private set; }

        public Task<WorkflowResponse> ImportAsync(WorkflowBundle bundle, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    private static (WorkflowImportController Controller, ImportDraft Draft, StubImporter Importer)
        Build(WorkflowResponse imported)
    {
        var cache = new ImportDraftCache();
        var caller = new FakeUser();
        var importer = new StubImporter { Response = imported };

        var draft = cache.Create(caller.UserId, "", Encoding.UTF8.GetBytes(BundleJson));
        draft.MarkAnalyzing();
        draft.MarkReady(new AnalysisReport
        {
            FormatDetected = WorkflowBundle.KindMarker,
            Confidence = 1.0,
            ProposedWorkflow = JsonDocument.Parse("""{"nodes":[],"edges":[]}""").RootElement.Clone(),
            // Its presence is what routes the commit to the bundle importer.
            Bundle = new BundlePlan { Kind = WorkflowBundle.KindMarker, SchemaVersion = "v3" },
        });

        var controller = new WorkflowImportController(
            cache,
            new NoScopes(),
            caller,
            new FakeAudit(),
            TestDb.NewContext(),
            new FakeSchemaValidator(),
            new FakeReferenceValidator(),
            new SnippetStubBuilder(),
            new AllowAll(),
            new DefaultSettings(),
            importer,
            new FakePolicyEvaluator(),
            NullLogger<WorkflowImportController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, draft, importer);
    }

    private sealed class AllowAll : flow_weaver_backend.Services.Permission.IResourcePermissionService
    {
        public Task<IReadOnlyList<ResourcePermissionResponse>> ListAsync(string t, Guid id, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ResourcePermissionResponse> GrantAsync(
            string t, Guid id, GrantResourcePermissionRequest dto, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RevokeAsync(Guid permissionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasAtLeastAsync(string t, Guid id, string requiredRole, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class DefaultSettings : flow_weaver_backend.Services.Settings.IAppSettingsService
    {
        public Task<flow_weaver_backend.Services.Settings.AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(flow_weaver_backend.Services.Settings.AppSettings.Default);
        public Task<flow_weaver_backend.Services.Settings.AppSettings> UpdateAsync(
            flow_weaver_backend.Services.Settings.AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }

    private static Dictionary<string, JsonElement> Payload(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    [Fact]
    public async Task A_bundle_commit_answers_in_the_wizard_shape()
    {
        var wid = Guid.NewGuid();
        var (controller, draft, _) = Build(new WorkflowResponse
        {
            WorkflowId = wid,
            Name = "imported",
            Environment = "draft",
            // As the importer really returns them: null, not empty.
            Warnings = null,
            ImportNotes = null,
        });

        var body = Payload(await controller.Commit(draft.Token, new CommitImportRequest(), default));

        Assert.Equal(
            new[] { "environment", "name", "warnings", "workflow_id" },
            body.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(wid.ToString(), body["workflow_id"].GetString());
        Assert.Equal("imported", body["name"].GetString());
        Assert.Equal("draft", body["environment"].GetString());
    }

    [Fact]
    public async Task Warnings_is_always_an_array_never_null()
    {
        // The whole bug in one assertion: the client iterates this field.
        var (controller, draft, _) = Build(new WorkflowResponse
        {
            WorkflowId = Guid.NewGuid(),
            Name = "imported",
            Environment = "draft",
            Warnings = null,
            ImportNotes = null,
        });

        var body = Payload(await controller.Commit(draft.Token, new CommitImportRequest(), default));

        Assert.Equal(JsonValueKind.Array, body["warnings"].ValueKind);
        Assert.Empty(body["warnings"].EnumerateArray());
    }

    [Fact]
    public async Task The_resolution_notes_reach_the_operator()
    {
        // They name what still has to be configured here — a credential this
        // instance must hold, a trigger left disabled. Returning the wrong shape
        // was hiding them as well as breaking the screen.
        var (controller, draft, _) = Build(new WorkflowResponse
        {
            WorkflowId = Guid.NewGuid(),
            Name = "imported",
            Environment = "draft",
            ImportNotes = ["credential 'lab-ssh' must be configured here"],
            Warnings = ["trigger 'nightly' was created disabled"],
        });

        var body = Payload(await controller.Commit(draft.Token, new CommitImportRequest(), default));
        var warnings = body["warnings"].EnumerateArray().Select(w => w.GetString()).ToList();

        Assert.Contains("credential 'lab-ssh' must be configured here", warnings);
        Assert.Contains("trigger 'nightly' was created disabled", warnings);
    }

    [Fact]
    public async Task Keeping_the_existing_workflow_does_not_import_the_bundle()
    {
        // `keep_existing` cancels the import. Routing it to the importer anyway
        // would create the very workflow the user just declined.
        var (controller, draft, importer) = Build(new WorkflowResponse { WorkflowId = Guid.NewGuid() });

        var result = await controller.Commit(
            draft.Token, new CommitImportRequest { ConflictResolution = "keep_existing" }, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, importer.Calls);
    }

    [Fact]
    public async Task A_committed_bundle_draft_cannot_be_committed_twice()
    {
        var (controller, draft, importer) = Build(new WorkflowResponse
        {
            WorkflowId = Guid.NewGuid(),
            Name = "imported",
            Environment = "draft",
        });

        await controller.Commit(draft.Token, new CommitImportRequest(), default);
        var second = await controller.Commit(draft.Token, new CommitImportRequest(), default);

        Assert.Equal(1, importer.Calls);
        Assert.IsNotType<OkObjectResult>(second);
    }
}

internal sealed class NoScopes : IServiceScopeFactory
{
    private readonly IServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
    public IServiceScope CreateScope() => _provider.CreateScope();
}
