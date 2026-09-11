using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Slo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace flow_weaver_backend.Tests;

internal static class HttpResult
{
    // 404 comes back as either NotFoundResult (no body) or NotFoundObjectResult.
    public static int? Status(IActionResult? r) => (r as IStatusCodeActionResult)?.StatusCode;
}

// Controllers that query AppDbContext directly (no service layer). Exercised
// over an InMemory DB with a DefaultHttpContext so any Response/Request access
// is safe. Read/aggregation endpoints return empty/zero on an empty store.
public class AdminMetricsControllerTests
{
    private readonly FakeUser _caller = new();
    private static ControllerContext Ctx() => new() { HttpContext = new DefaultHttpContext() };

    [Fact]
    public async Task Metrics_endpoints_return_results()
    {
        using var db = TestDb.NewContext();
        var c = new AdminMetricsController(db, _caller) { ControllerContext = Ctx() };

        var slo = new SloComputeService(new SloRepository(db));
        Assert.NotNull((object?)(await c.Runs(7, CancellationToken.None)).Value ?? (await c.Runs(7, CancellationToken.None)).Result);
        Assert.NotNull((object?)(await c.Slo(7, slo, CancellationToken.None)).Value ?? (await c.Slo(7, slo, CancellationToken.None)).Result);
        Assert.NotNull((object?)(await c.Auth(7, CancellationToken.None)).Value ?? (await c.Auth(7, CancellationToken.None)).Result);
    }
}

public class QaLabControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Dashboard_returns_result()
    {
        using var db = TestDb.NewContext();
        var c = new QaLabController(db, _caller) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

        var res = await c.Dashboard(CancellationToken.None);
        Assert.NotNull((object?)res.Value ?? res.Result);
    }
}

public class TraceEventsControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Summary_and_get_forward()
    {
        using var db = TestDb.NewContext();
        var c = new TraceEventsController(db, _caller) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

        var summary = await c.Summary(24, CancellationToken.None);
        Assert.NotNull((object?)summary.Value ?? summary.Result);
        Assert.Equal(404, HttpResult.Status((await c.Get(Guid.NewGuid(), CancellationToken.None)).Result));
    }
}

public class AiConversationsControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Get_missing_returns_404()
    {
        using var db = TestDb.NewContext();
        var c = new AiConversationsController(db, _caller) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

        Assert.Equal(404, HttpResult.Status((await c.Get(Guid.NewGuid(), CancellationToken.None)).Result));
    }
}
