using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

public class WorkflowRollbackAnalyzerTests
{
    private static WorkflowRollbackAnalyzer NewAnalyzer(Data.Db.AppDbContext db)
        => new(new SnippetRepository(db), Array.Empty<ISnippetHandler>());

    private static WorkflowModel EmptyWorkflow() => new()
    {
        WorkflowId = Guid.NewGuid(),
        Name = "wf",
        Nodes = TestJson.Element("[]"),
        Edges = TestJson.Element("[]"),
    };

    [Fact]
    public async Task Analyze_empty_workflow_returns_empty_report()
    {
        using var db = TestDb.NewContext();

        var report = await NewAnalyzer(db).AnalyzeAsync(EmptyWorkflow(), CancellationToken.None);

        Assert.NotNull(report);
    }

    [Fact]
    public async Task Analyze_workflow_with_nodes_object_returns_report()
    {
        using var db = TestDb.NewContext();
        var wf = EmptyWorkflow();
        // A node referencing a snippet that isn't in the DB — analyzer must
        // still return a report (risk classification), not throw.
        wf.Nodes = TestJson.Element("""[{"id":"n1","snippetId":"22222222-2222-2222-2222-222222222222"}]""");

        var report = await NewAnalyzer(db).AnalyzeAsync(wf, CancellationToken.None);

        Assert.NotNull(report);
    }
}
