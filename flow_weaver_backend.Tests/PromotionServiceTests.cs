using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Promotion;
using flow_weaver_backend.Services.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SimulationResultModel = flow_weaver_backend.Models.SimulationResult;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

public class PromotionServiceTests
{
    private readonly FakeUser _caller = new();

    private static PromotionService NewSvc(AppDbContext db, FakeUser user)
        => new(
            new WorkflowRepository(db),
            new WorkflowVersionRepository(db),
            new RepositoryBase<SimulationResultModel>(db),
            new UnitOfWork(db),
            user,
            new FakeAudit(),
            new FakeTrace(),
            new FakePolicyEvaluator(),
            new WorkflowRollbackAnalyzer(new SnippetRepository(db), Array.Empty<ISnippetHandler>()),
            new flow_weaver_backend.Services.Permission.EffectivePermissions(user, new PermissionGrantReader(db)),
            new FakeAppSettings(),
            NullLogger<PromotionService>.Instance);

    [Fact]
    public async Task Diff_missing_workflow_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).DiffAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(res.Result).StatusCode);
    }

    [Fact]
    public async Task Clone_missing_workflow_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).CloneAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(res.Result).StatusCode);
    }
}
