using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.InventorySource;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;

namespace flow_weaver_backend.Tests;

public class InventorySourceServiceTests
{
    private readonly FakeUser _caller = new();

    private static InventorySourceService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<InventorySourceModel>(db), user, new FakeAudit(), NullLogger<InventorySourceService>.Instance);

    private static CreateInventorySource Sample(string name = "netbox-prod", string type = "netbox")
        => new() { Name = name, Type = type };

    private static Guid CreatedId(ActionResult<InventorySourceResponse> r)
        => ((InventorySourceResponse)((CreatedAtActionResult)r.Result!).Value!).InventorySourceId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var result = await svc.PostAsync(Sample());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<InventorySourceResponse>(created.Value);
        Assert.Equal("netbox-prod", body.Name);
        Assert.Single(db.InventorySources);
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var result = await svc.PostAsync(new CreateInventorySource { Name = "", Type = "netbox" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_type()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var result = await svc.PostAsync(new CreateInventorySource { Name = "src", Type = "  " });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var got = await svc.GetByIdAsync(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(got.Result);
    }

    [Fact]
    public async Task Get_lists_rows()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample("a"));
        await svc.PostAsync(Sample("b"));

        var list = await svc.GetAsync();

        var ok = Assert.IsType<OkObjectResult>(list.Result);
        var page = Assert.IsType<ListResponse<InventorySourceResponse>>(ok.Value);
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Update_renames()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        var res = await svc.UpdateAsync(id, new UpdateInventorySource { Name = "renamed" });

        var body = Assert.IsType<InventorySourceResponse>(res.Value);
        Assert.Equal("renamed", body.Name);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var res = await svc.UpdateAsync(Guid.NewGuid(), new UpdateInventorySource { Name = "x" });

        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        await svc.DeleteAsync(id);

        Assert.False(db.InventorySources.Single().IsActive);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var res = await svc.DeleteAsync(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(res.Result);
    }
}
