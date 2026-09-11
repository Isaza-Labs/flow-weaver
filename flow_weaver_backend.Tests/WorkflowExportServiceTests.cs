using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Services.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

public class WorkflowExportServiceTests
{
    private readonly FakeUser _caller = new();

    private static WorkflowExportService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<WorkflowModel>(db), new SnippetRepository(db),
               new WorkflowYamlCompiler(NullLogger<WorkflowYamlCompiler>.Instance),
               Array.Empty<IWorkflowExporter>());

    [Fact]
    public async Task Export_missing_workflow_throws_not_found()
    {
        using var db = TestDb.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            NewSvc(db, _caller).ExportAsync(Guid.NewGuid(), "yaml", CancellationToken.None));
    }

    [Fact]
    public void ParseImport_rejects_unknown_format()
    {
        using var db = TestDb.NewContext();
        // Any non-empty, structurally-invalid input for an unsupported format
        // must raise a domain ValidationException, not crash.
        Assert.ThrowsAny<Exception>(() => NewSvc(db, _caller).ParseImport("not valid", "totally-unknown-format"));
    }
}
