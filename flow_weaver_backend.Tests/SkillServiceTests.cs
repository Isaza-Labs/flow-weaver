using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Skill;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SkillModel = flow_weaver_backend.Models.Skill;

namespace flow_weaver_backend.Tests;

public class SkillServiceTests
{
    private readonly FakeUser _caller = new();

    private static SkillService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<SkillModel>(db), user, NullLogger<SkillService>.Instance);

    private static CreateSkill Sample(string name = "reboot") => new() { Name = name, SkillType = "action" };

    private static Guid CreatedId(ActionResult<SkillResponse> r)
        => ((SkillResponse)((CreatedAtActionResult)r.Result!).Value!).SkillId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<SkillResponse>(created.Value);
        Assert.Equal("reboot", body.Name);
        Assert.Single(db.Set<SkillModel>());
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(new CreateSkill { Name = "", SkillType = "action" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_skill_type()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(new CreateSkill { Name = "x", SkillType = "  " });
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
        var page = Assert.IsType<ListResponse<SkillResponse>>(ok.Value);
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateSkill());
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));
        await svc.DeleteAsync(id);
        Assert.False(db.Set<SkillModel>().Single().IsActive);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var res = await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid());
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }
}
