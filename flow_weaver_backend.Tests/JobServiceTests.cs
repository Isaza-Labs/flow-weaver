using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Job;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using JobModel = flow_weaver_backend.Models.Job;

namespace flow_weaver_backend.Tests;

public class JobServiceTests
{
    private readonly FakeUser _caller = new();

    private static JobService NewSvc(AppDbContext db, FakeUser user)
        => new(new JobRepository(db), user, NullLogger<JobService>.Instance);

    private static Guid SeedJob(AppDbContext db, string status = "pending")
    {
        var id = Guid.NewGuid();
        db.Set<JobModel>().Add(new JobModel
        {
            JobId = id,
            Type = "workflow",
            Tag = "default",
            Status = status,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Get_lists_jobs()
    {
        using var db = TestDb.NewContext();
        SeedJob(db);
        SeedJob(db);

        var ok = Assert.IsType<OkObjectResult>((await NewSvc(db, _caller).GetAsync()).Result);
        Assert.Equal(2, Assert.IsType<ListResponse<JobResponse>>(ok.Value).Total);
    }

    [Fact]
    public async Task GetById_returns_job()
    {
        using var db = TestDb.NewContext();
        var id = SeedJob(db);

        var got = await NewSvc(db, _caller).GetByIdAsync(id);

        Assert.IsType<JobResponse>(got.Value);
    }

    [Fact]
    public async Task GetById_not_found_returns_404()
    {
        using var db = TestDb.NewContext();
        Assert.IsType<NotFoundObjectResult>((await NewSvc(db, _caller).GetByIdAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task GetQueueStats_returns_a_result()
    {
        using var db = TestDb.NewContext();
        SeedJob(db, "pending");
        SeedJob(db, "running");

        var res = await NewSvc(db, _caller).GetQueueStatsAsync();

        Assert.NotNull((object?)res.Value ?? res.Result);
    }
}
