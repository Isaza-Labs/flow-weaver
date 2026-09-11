using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Policy;
using PolicyModel = flow_weaver_backend.Models.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Round-trip tests for the CRUD layer. The service is thin enough that
// integration-ish coverage (create → list → update → delete) is both
// faster to write and more useful than unit-testing each method in
// isolation — the active-row query is the only bit worth exercising
// carefully and it's the same code path for every verb.
public class PolicyServiceTests
{
    private readonly FakeUser _caller = new();

    private AppDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private static CreatePolicy SampleCreate(string name = "no-prod-writes") => new()
    {
        Name = name,
        Description = "test policy",
        Rule = JsonDocument.Parse("""{"action":"deny","when":{"env":["production"]}}""").RootElement,
        Enabled = true,
    };

    [Fact]
    public async Task Post_persists_and_returns_created()
    {
        using var db = NewContext(nameof(Post_persists_and_returns_created));
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);

        var result = await svc.PostAsync(SampleCreate());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<PolicyResponse>(created.Value);
        Assert.Equal("no-prod-writes", body.Name);
        Assert.True(body.Enabled);
        Assert.Single(db.Policies);
    }

    [Fact]
    public async Task Post_rejects_empty_name()
    {
        using var db = NewContext(nameof(Post_rejects_empty_name));
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);

        var dto = SampleCreate();
        dto.Name = "";
        var result = await svc.PostAsync(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Post_rejects_non_object_rule()
    {
        using var db = NewContext(nameof(Post_rejects_non_object_rule));
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);

        var dto = SampleCreate();
        dto.Rule = JsonDocument.Parse("\"not-an-object\"").RootElement;
        var result = await svc.PostAsync(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_toggles_enabled_flag()
    {
        using var db = NewContext(nameof(Update_toggles_enabled_flag));
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);
        var created = Assert.IsType<CreatedAtActionResult>(
            (await svc.PostAsync(SampleCreate())).Result);
        var id = ((PolicyResponse)created.Value!).PolicyId;

        var update = new UpdatePolicy { Enabled = false };
        var result = await svc.UpdateAsync(id, update);

        var body = Assert.IsType<PolicyResponse>(result.Value);
        Assert.False(body.Enabled);
    }

    [Fact]
    public async Task Delete_soft_deletes_row()
    {
        using var db = NewContext(nameof(Delete_soft_deletes_row));
        var svc = new PolicyService(new RepositoryBase<PolicyModel>(db), _caller, new FakeAudit(), NullLogger<PolicyService>.Instance);
        var created = Assert.IsType<CreatedAtActionResult>(
            (await svc.PostAsync(SampleCreate())).Result);
        var id = ((PolicyResponse)created.Value!).PolicyId;

        await svc.DeleteAsync(id);

        // The row still exists on disk but IsActive = false, so list
        // queries won't return it.
        var row = db.Policies.Single();
        Assert.False(row.IsActive);

        var listResult = await svc.GetAsync();
        var ok = Assert.IsType<OkObjectResult>(listResult.Result);
        var page = Assert.IsType<ListResponse<PolicyResponse>>(ok.Value);
        Assert.Empty(page.Data);
    }

}
