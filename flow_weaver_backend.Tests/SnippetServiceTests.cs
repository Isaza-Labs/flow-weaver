using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Snippet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Tests;

public class SnippetServiceTests
{
    private readonly FakeUser _caller = new();

    private static SnippetService NewSvc(AppDbContext db, FakeUser user)
        => new(new SnippetRepository(db), new StepRunRepository(db), user,
               new FakeAudit(), new FakeTrace(), NullLogger<SnippetService>.Instance);

    private static CreateSnippet Sample(string name = "ping-device")
        => new() { Name = name, Type = "rest_call", TargetMode = "per_device" };

    private static Guid CreatedId(ActionResult<SnippetResponse> r)
        => ((SnippetResponse)((CreatedAtActionResult)r.Result!).Value!).SnippetId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<SnippetResponse>(created.Value);
        Assert.Equal("ping-device", body.Name);
        Assert.Single(db.Set<SnippetModel>());
    }

    // Attribution: this path serves the UI and the agent's create_snippet
    // tool — both used to leave created_by null (only the import pipeline
    // stamped it).
    [Fact]
    public async Task Post_stamps_created_by_from_caller()
    {
        using var db = TestDb.NewContext();
        await NewSvc(db, _caller).PostAsync(Sample());
        var row = db.Set<SnippetModel>().Single();
        Assert.Equal("tester", row.CreatedBy);
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateSnippet { Name = "", Type = "rest_call", TargetMode = "per_device" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_type()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateSnippet { Name = "n", Type = "", TargetMode = "per_device" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_target_mode()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateSnippet { Name = "n", Type = "rest_call", TargetMode = "" });
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
        Assert.Equal(2, Assert.IsType<ListResponse<SnippetResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateSnippet())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));
        await svc.DeleteAsync(id);
        Assert.False(db.Set<SnippetModel>().Single().IsActive);
    }
}
