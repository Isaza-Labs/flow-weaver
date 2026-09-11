using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Device;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// CRUD round-trip for DeviceService against an InMemory AppDbContext.
// Covers the happy path plus every early-return branch (validation, 404) —
// those branches are where coverage otherwise hides.
public class DeviceServiceTests
{
    private readonly FakeUser _caller = new();

    private static DeviceService NewSvc(AppDbContext db, FakeUser user)
        => new(new DeviceRepository(db), user, new FakeAudit(), NullLogger<DeviceService>.Instance);

    private static CreateDevice Sample(string name = "core-sw-1") => new() { DeviceName = name };

    private static Guid CreatedId(ActionResult<DeviceResponse> r)
        => ((DeviceResponse)((CreatedAtActionResult)r.Result!).Value!).DeviceId;

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var result = await svc.PostAsync(Sample());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<DeviceResponse>(created.Value);
        Assert.Equal("core-sw-1", body.DeviceName);
        Assert.Single(db.Devices);
    }

    [Fact]
    public async Task Post_rejects_blank_name()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var result = await svc.PostAsync(new CreateDevice { DeviceName = "  " });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.Devices);
    }

    [Fact]
    public async Task GetById_returns_device()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        var got = await svc.GetByIdAsync(id);

        var body = Assert.IsType<DeviceResponse>(got.Value);
        Assert.Equal(id, body.DeviceId);
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
    public async Task Get_lists_active_rows()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        await svc.PostAsync(Sample("a"));
        await svc.PostAsync(Sample("b"));

        var list = await svc.GetAsync();

        var ok = Assert.IsType<OkObjectResult>(list.Result);
        var page = Assert.IsType<ListResponse<DeviceResponse>>(ok.Value);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Data.Count);
    }

    [Fact]
    public async Task Update_modifies_field()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        var res = await svc.UpdateAsync(id, new UpdateDevice { IpAddress = "10.0.0.9" });

        var body = Assert.IsType<DeviceResponse>(res.Value);
        Assert.Equal("10.0.0.9", body.IpAddress);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);

        var res = await svc.UpdateAsync(Guid.NewGuid(), new UpdateDevice { IpAddress = "1.1.1.1" });

        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Delete_soft_deletes_and_hides_from_list()
    {
        using var db = TestDb.NewContext();
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostAsync(Sample()));

        await svc.DeleteAsync(id);

        var list = await svc.GetAsync();
        var page = (ListResponse<DeviceResponse>)((OkObjectResult)list.Result!).Value!;
        Assert.Equal(0, page.Total);
        Assert.False(db.Devices.Single().IsActive);
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
