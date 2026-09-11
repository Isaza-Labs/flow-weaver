using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.VendorCommand;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Tests;

public class VendorCommandServiceTests
{
    private readonly FakeUser _caller = new();

    private static VendorCommandService NewSvc(AppDbContext db, FakeUser user)
        => new(new VendorCommandRepository(db), user, new FakeAudit(), new FakeTrace(),
               new FakeVendorCommandRegistry(), NullLogger<VendorCommandService>.Instance);

    private static CreateVendorCommand Sample(string value = "show version")
        => new() { DeviceType = "cisco_ios", Value = value };

    private static Guid CreatedId(ActionResult<VendorCommandResponse> r)
        => ((VendorCommandResponse)((CreatedAtActionResult)r.Result!).Value!).VendorCommandId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var result = await NewSvc(db, _caller).PostAsync(Sample());
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.IsType<VendorCommandResponse>(created.Value);
        Assert.Single(db.Set<VendorCommandModel>());
    }

    [Fact]
    public async Task Post_rejects_blank_device_type()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateVendorCommand { DeviceType = "", Value = "show version" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_blank_value()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostAsync(new CreateVendorCommand { DeviceType = "cisco_ios", Value = "" });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task Post_rejects_duplicate()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample());
        var dup = await svc.PostAsync(Sample());
        Assert.IsType<BadRequestObjectResult>(dup.Result);
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
        await svc.PostAsync(Sample("show version"));
        await svc.PostAsync(Sample("show running-config"));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync(null)).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<VendorCommandResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateVendorCommand())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));
        await svc.DeleteAsync(id);
        Assert.False(db.Set<VendorCommandModel>().Single().IsActive);
    }
}
