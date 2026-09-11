using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.AIAgent;
using flow_weaver_backend.Services.Device;
using flow_weaver_backend.Services.DevicePool;
using flow_weaver_backend.Services.InventorySource;
using flow_weaver_backend.Services.Skill;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIAgentModel = flow_weaver_backend.Models.AIAgent;
using DevicePoolModel = flow_weaver_backend.Models.DevicePool;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;
using SkillModel = flow_weaver_backend.Models.Skill;

namespace flow_weaver_backend.Tests;

// Controllers are thin forwarders. These tests construct the real service over
// an InMemory DB, then drive every endpoint through the controller to cover the
// forwarding lines (+ any inline controller logic). No HTTP host / auth pipeline.

public class DeviceControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new DeviceController(new DeviceService(new DeviceRepository(db), _caller, new FakeAudit(), NullLogger<DeviceService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateDevice { DeviceName = "sw1" })).Result);
        var id = ((DeviceResponse)created.Value!).DeviceId;
        Assert.IsType<DeviceResponse>((await c.GetById(id)).Value);
        Assert.IsType<DeviceResponse>((await c.Update(id, new UpdateDevice { IpAddress = "10.0.0.2" })).Value);
        Assert.IsType<DeviceResponse>((await c.Delete(id)).Value);
        Assert.IsType<NotFoundObjectResult>((await c.GetById(Guid.NewGuid())).Result);
    }
}

public class DevicePoolControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new DevicePoolController(new DevicePoolService(new RepositoryBase<DevicePoolModel>(db), _caller, new FakeAudit(), NullLogger<DevicePoolService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateDevicePool { Name = "pool" })).Result);
        var id = ((DevicePoolResponse)created.Value!).DevicePoolId;
        Assert.IsType<DevicePoolResponse>((await c.GetById(id)).Value);
        Assert.IsType<DevicePoolResponse>((await c.Update(id, new UpdateDevicePool { Name = "renamed" })).Value);
        Assert.IsType<DevicePoolResponse>((await c.Delete(id)).Value);
    }
}

public class InventorySourceControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new InventorySourceController(new InventorySourceService(new RepositoryBase<InventorySourceModel>(db), _caller, new FakeAudit(), NullLogger<InventorySourceService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateInventorySource { Name = "nb", Type = "netbox" })).Result);
        var id = ((InventorySourceResponse)created.Value!).InventorySourceId;
        Assert.IsType<InventorySourceResponse>((await c.GetById(id)).Value);
        Assert.IsType<InventorySourceResponse>((await c.Update(id, new UpdateInventorySource { Name = "x" })).Value);
        Assert.IsType<InventorySourceResponse>((await c.Delete(id)).Value);
    }
}

public class SkillControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new SkillController(new SkillService(new RepositoryBase<SkillModel>(db), _caller, NullLogger<SkillService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateSkill { Name = "s", SkillType = "action" })).Result);
        var id = ((SkillResponse)created.Value!).SkillId;
        Assert.IsType<SkillResponse>((await c.GetById(id)).Value);
        Assert.IsType<SkillResponse>((await c.Delete(id)).Value);
    }
}

public class AIAgentControllerTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Endpoints_forward_to_service()
    {
        using var db = TestDb.NewContext();
        var c = new AIAgentController(new AIAgentService(new RepositoryBase<AIAgentModel>(db), _caller, NullLogger<AIAgentService>.Instance));

        Assert.IsType<OkObjectResult>((await c.Get()).Result);
        var created = Assert.IsType<CreatedAtActionResult>((await c.Post(new CreateAIAgent { Name = "a", Role = "assistant" })).Result);
        var id = ((AIAgentResponse)created.Value!).AIAgentId;
        Assert.IsType<AIAgentResponse>((await c.GetById(id)).Value);
        Assert.IsType<AIAgentResponse>((await c.Delete(id)).Value);
    }
}
