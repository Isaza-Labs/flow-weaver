using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AIProvider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIProviderModel = flow_weaver_backend.Models.AIProvider;

namespace flow_weaver_backend.Tests;

public class AIProviderServiceTests
{
    private readonly FakeUser _caller = new();

    private static AIProviderService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<AIProviderModel>(db), new FakeCrypto(), user, new FakeAudit(), NullLogger<AIProviderService>.Instance);

    private static CreateAIProvider Sample(string name = "openai-prod") =>
        new() { Name = name, Type = "openai", DefaultModel = "gpt-4o", APIKey = "sk-secret" };

    private static Guid CreatedId(ActionResult<AIProviderResponse> r)
        => ((AIProviderResponse)((CreatedAtActionResult)r.Result!).Value!).AIProviderId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<AIProviderResponse>(created.Value);
        Assert.Equal("openai-prod", body.Name);
        Assert.Single(db.Set<AIProviderModel>());
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAIProvider { Name = "", Type = "openai", DefaultModel = "gpt-4o" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_type()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAIProvider { Name = "x", Type = "", DefaultModel = "gpt-4o" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_default_model()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAIProvider { Name = "x", Type = "openai", DefaultModel = "  " });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Get_lists_rows()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample("a"));
        await svc.PostAsync(Sample("b"));
        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<AIProviderResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateAIProvider())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));
        await svc.DeleteAsync(id);
        Assert.False(db.Set<AIProviderModel>().Single().IsActive);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid())).Result);
    }
}
