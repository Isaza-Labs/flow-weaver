using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.IntegrationAction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using IntegrationActionModel = flow_weaver_backend.Models.IntegrationAction;
using IntegrationModel = flow_weaver_backend.Models.Integration;

namespace flow_weaver_backend.Tests;

public class IntegrationActionServiceTests
{
    private readonly FakeUser _caller = new();

    private static IntegrationActionService NewSvc(AppDbContext db, FakeUser user)
        => new(new RepositoryBase<IntegrationActionModel>(db), new IntegrationRepository(db),
               user, NullLogger<IntegrationActionService>.Instance);

    private static Guid SeedIntegration(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Set<IntegrationModel>().Add(new IntegrationModel
        {
            IntegrationId = id,
            Name = "netbox",
            Type = "rest",
            BaseURL = "https://nb.example",
            Status = "active",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static CreateIntegrationAction Sample(string name = "get-devices")
        => new() { Name = name, Method = "GET", Path = "/dcim/devices/" };

    private static Guid CreatedId(ActionResult<IntegrationActionResponse> r)
        => ((IntegrationActionResponse)((CreatedAtActionResult)r.Result!).Value!).IntegrationActionId;

    [Fact]
    public async Task PostForIntegration_persists()
    {
        using var db = TestDb.NewContext();
        var integrationId = SeedIntegration(db);

        var result = await NewSvc(db, _caller).PostForIntegrationAsync(integrationId, Sample());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.IsType<IntegrationActionResponse>(created.Value);
        Assert.Single(db.Set<IntegrationActionModel>());
    }

    [Theory]
    [InlineData("", "GET", "/x")]
    [InlineData("n", "", "/x")]
    [InlineData("n", "GET", "")]
    public async Task PostForIntegration_rejects_missing_required(string name, string method, string path)
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForIntegrationAsync(
            Guid.NewGuid(), new CreateIntegrationAction { Name = name, Method = method, Path = path });
        Assert.IsType<BadRequestObjectResult>(r.Result);
    }

    [Fact]
    public async Task PostForIntegration_404_when_integration_missing()
    {
        using var db = TestDb.NewContext();
        var r = await NewSvc(db, _caller).PostForIntegrationAsync(Guid.NewGuid(), Sample());
        Assert.IsType<NotFoundObjectResult>(r.Result);
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
        var integrationId = SeedIntegration(db);
        var svc = NewSvc(db, _caller);
        await svc.PostForIntegrationAsync(integrationId, Sample("a"));
        await svc.PostForIntegrationAsync(integrationId, Sample("b"));

        var ok = Assert.IsType<OkObjectResult>((await svc.GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<IntegrationActionResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task Update_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).UpdateAsync(Guid.NewGuid(), new UpdateIntegrationAction())).Result);
    }

    [Fact]
    public async Task Delete_soft_deletes()
    {
        using var db = TestDb.NewContext();
        var integrationId = SeedIntegration(db);
        var svc = NewSvc(db, _caller);
        var id = CreatedId(await svc.PostForIntegrationAsync(integrationId, Sample()));

        await svc.DeleteAsync(id);

        Assert.False(db.Set<IntegrationActionModel>().Single().IsActive);
    }

    [Fact]
    public async Task Delete_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).DeleteAsync(Guid.NewGuid())).Result);
    }
}
