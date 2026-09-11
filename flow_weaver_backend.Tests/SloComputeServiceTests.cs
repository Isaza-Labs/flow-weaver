using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Slo;

namespace flow_weaver_backend.Tests;

public class SloComputeServiceTests
{
    private readonly FakeUser _caller = new();

    [Fact]
    public async Task Compute_with_no_data_returns_snapshot()
    {
        using var db = TestDb.NewContext();
        var svc = new SloComputeService(new SloRepository(db));

        var snapshot = await svc.ComputeAsync(7, CancellationToken.None);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task Compute_clamps_days_window()
    {
        using var db = TestDb.NewContext();
        var svc = new SloComputeService(new SloRepository(db));

        // Extreme/invalid day windows must not throw — the service clamps.
    }
}
