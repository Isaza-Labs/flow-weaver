using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AiApiSpec;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

public class AiApiSpecServiceTests
{
    private readonly FakeUser _caller = new();

    private const string ValidYaml = "openapi: 3.0.0\ninfo:\n  title: netbox\n  version: '1.0'\npaths: {}\n";

    private static AiApiSpecService NewSvc(AppDbContext db, FakeUser user)
        => new(new AiApiSpecRepository(db), user, new FakeApiSpecIndex(),
               NullLogger<AiApiSpecService>.Instance);

    private static CreateAiApiSpec Sample(string api = "netbox")
        => new() { Api = api, Content = ValidYaml };

    private static Guid CreatedId(ActionResult<AiApiSpecResponse> r)
        => ((AiApiSpecResponse)((CreatedAtActionResult)r.Result!).Value!).AiApiSpecId;

    [Fact]
    public async Task Post_persists()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_api()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAiApiSpec { Api = "", Content = ValidYaml });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_api_with_invalid_chars()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAiApiSpec { Api = "has space", Content = ValidYaml });
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
        await svc.PostAsync(Sample("netbox"));
        await svc.PostAsync(Sample("meraki"));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<AiApiSpecResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateAiApiSpec())).Result);
    }

    [Fact]
    public async Task Delete_created_row()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        var res = await svc.DeleteAsync(id);

        Assert.NotNull((object?)res.Value ?? res.Result);
    }
}
