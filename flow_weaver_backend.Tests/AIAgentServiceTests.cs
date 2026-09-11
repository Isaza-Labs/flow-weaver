using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AIAgent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Tests;

public class AIAgentServiceTests
{
    private readonly FakeUser _caller = new();

    private static AIAgentService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<AIAgentModel>(db), user, NullLogger<AIAgentService>.Instance);

    private static CreateAIAgent Sample(string name = "netops") => new() { Name = name, Role = "assistant" };

    private static Guid CreatedId(ActionResult<AIAgentResponse> r)
        => ((AIAgentResponse)((CreatedAtActionResult)r.Result!).Value!).AIAgentId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<AIAgentResponse>(created.Value);
        Assert.Equal("netops", body.Name);
        Assert.Single(db.Set<AIAgentModel>());
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(new CreateAIAgent { Name = "", Role = "assistant" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_role()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(new CreateAIAgent { Name = "x", Role = "  " });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var got = await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid());
        Assert.IsType<NotFoundObjectResult>(got.Result);
    }

    [Fact]
    public async Task Get_lists_rows()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample("a"));
        await svc.PostAsync(Sample("b"));
        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<AIAgentResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateAIAgent())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));
        await svc.DeleteAsync(id);
        Assert.False(db.Set<AIAgentModel>().Single().IsActive);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid())).Result);
    }
}
