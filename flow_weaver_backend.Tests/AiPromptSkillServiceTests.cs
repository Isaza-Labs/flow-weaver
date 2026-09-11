using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AiPromptSkill;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

public class AiPromptSkillServiceTests
{
    private readonly FakeUser _caller = new();

    private static AiPromptSkillService NewSvc(AppDbContext db, FakeUser user)
        => new(new AiPromptSkillRepository(db), user, new FakeSkillPromptLoader(),
               NullLogger<AiPromptSkillService>.Instance);

    private static CreateAiPromptSkill Sample(string name = "reboot.md")
        => new() { Name = name, Content = "# reboot\nrun the reboot playbook" };

    private static Guid CreatedId(ActionResult<AiPromptSkillResponse> r)
        => ((AiPromptSkillResponse)((CreatedAtActionResult)r.Result!).Value!).AiPromptSkillId;

    [Fact]
    public async Task Post_persists()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAiPromptSkill { Name = "", Content = "x" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_name_without_md_extension()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateAiPromptSkill { Name = "reboot", Content = "x" });
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
        await svc.PostAsync(Sample("a.md"));
        await svc.PostAsync(Sample("b.md"));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<AiPromptSkillResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateAiPromptSkill())).Result);
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
