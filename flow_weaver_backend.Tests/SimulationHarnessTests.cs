using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

// The load-test simulation harness. Covers the parts that
// ARE deterministically unit-testable: the fake device handlers (no real I/O +
// synthetic latency + failure injection), the production safety guard, and the
// bulk device/pool/workflow seeding. The actual capacity CURVES still require
// running this against a real Postgres staging box (the queue is raw SQL) — out
// of scope for an in-process test, by design.
public class SimulationHarnessTests
{

    private static SnippetRequest Req() => new()
    {
        StepRunId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        NodeId = "ping",
        SnippetId = Guid.NewGuid(),
        SnippetType = "ping",
        InputPayload = JsonDocument.Parse("{}").RootElement,
        DeviceId = Guid.NewGuid(),
    };

    // ── Simulated handlers ────────────────────────────────────────────────────

    [Fact]
    public async Task Sim_ping_handler_succeeds_with_simulated_output_and_no_real_io()
    {
        var handler = new SimPingHandler(Options.Create(
            new SimulationOptions { LatencyMs = 3, LatencyJitterMs = 0, FailureRate = 0 }));

        var result = await handler.ExecuteAsync(Req(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ping", handler.Type);
        Assert.True(result.Output.GetProperty("simulated").GetBoolean());
        Assert.Equal("ping", result.Output.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Sim_handler_applies_the_configured_latency()
    {
        var handler = new SimPingHandler(Options.Create(
            new SimulationOptions { LatencyMs = 40, LatencyJitterMs = 0, FailureRate = 0 }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await handler.ExecuteAsync(Req(), CancellationToken.None);
        sw.Stop();

        // Allow generous scheduler slack, but it must have actually slept.
        Assert.True(sw.ElapsedMilliseconds >= 30, $"expected ~40ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Sim_handler_failure_rate_1_always_fails()
    {
        var handler = new SimSshHandler(Options.Create(
            new SimulationOptions { LatencyMs = 0, LatencyJitterMs = 0, FailureRate = 1.0 }));

        var result = await handler.ExecuteAsync(Req(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("simulated", result.Error);
    }

    [Fact]
    public void Sim_handlers_mirror_the_real_idempotency_floors()
    {
        var opts = Options.Create(new SimulationOptions());
        Assert.Equal(IdempotencyKind.Idempotent, new SimPingHandler(opts).DefaultIdempotency);
        Assert.Equal(IdempotencyKind.NonReversible, new SimSshHandler(opts).DefaultIdempotency);
        Assert.Equal(IdempotencyKind.NonReversible, new SimAnsibleHandler(opts).DefaultIdempotency);
    }

    // ── Production safety guard ────────────────────────────────────────────────

    [Fact]
    public void Guard_refuses_simulation_on_a_production_tier()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SimulationGuard.ThrowIfUnsafe(simulationEnabled: true, workerEnvironment: "production"));
        Assert.Contains("production", ex.Message);
    }

    [Theory]
    [InlineData(true, "dev-sandbox")]
    [InlineData(true, "qa-lab")]
    [InlineData(true, null)]
    [InlineData(false, "production")]   // sim off → always fine
    public void Guard_allows_every_safe_combination(bool enabled, string? env)
    {
        // Must not throw.
        SimulationGuard.ThrowIfUnsafe(enabled, env);
    }

    // ── Bulk seeding ───────────────────────────────────────────────────────────

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static LoadTestController NewController(AppDbContext db, bool simEnabled) =>
        new(db, new FakeUser(),
            Options.Create(new SimulationOptions { Enabled = simEnabled }),
            NullLogger<LoadTestController>.Instance);

    [Fact]
    public async Task Seed_creates_devices_pool_and_a_runnable_workflow()
    {
        using var db = NewDb(nameof(Seed_creates_devices_pool_and_a_runnable_workflow));
        var controller = NewController(db, simEnabled: true);

        var result = await controller.Seed(new LoadTestController.SeedRequest { Count = 25 }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var pool = await db.DevicePools.FirstAsync();
        Assert.Equal(25, pool.StaticMembers.Count);

        // The generated workflow must be a structurally valid, runnable DAG.
        var wf = await db.Workflows.FirstAsync(w => w.Name == "loadtest-ping-fanout");
        var parser = new DagParser(NullLogger<DagParser>.Instance);
        var dag = parser.Parse(wf.Nodes, wf.Edges);   // throws DagParseException if malformed
        Assert.NotNull(dag);

        // And the ping snippet fans out per device.
        var snippet = await db.Snippets.FirstAsync(s => s.Name == "loadtest-ping");
        Assert.Equal("ping", snippet.Type);
        Assert.Equal("per_device", snippet.TargetMode);
    }

    [Fact]
    public async Task Seed_reuses_the_same_workflow_across_calls()
    {
        using var db = NewDb(nameof(Seed_reuses_the_same_workflow_across_calls));
        var controller = NewController(db, simEnabled: true);

        await controller.Seed(new LoadTestController.SeedRequest { Count = 5 }, CancellationToken.None);
        await controller.Seed(new LoadTestController.SeedRequest { Count = 5 }, CancellationToken.None);

        // One workflow + one snippet reused; two device batches + two pools.
        Assert.Equal(1, await db.Workflows.CountAsync(w => w.Name == "loadtest-ping-fanout"));
        Assert.Equal(1, await db.Snippets.CountAsync(s => s.Name == "loadtest-ping"));
        Assert.Equal(10, await db.Devices.CountAsync(d => d.ExternalId == "loadtest"));
    }

    [Fact]
    public async Task Seed_is_refused_when_simulation_is_disabled()
    {
        using var db = NewDb(nameof(Seed_is_refused_when_simulation_is_disabled));
        var controller = NewController(db, simEnabled: false);

        var result = await controller.Seed(new LoadTestController.SeedRequest { Count = 5 }, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.Equal(0, await db.Devices.CountAsync());   // nothing created
    }

    [Fact]
    public async Task Teardown_removes_the_seeded_data()
    {
        using var db = NewDb(nameof(Teardown_removes_the_seeded_data));
        var controller = NewController(db, simEnabled: true);
        await controller.Seed(new LoadTestController.SeedRequest { Count = 8 }, CancellationToken.None);

        var result = await controller.Teardown(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, await db.Devices.CountAsync(d => d.ExternalId == "loadtest"));
        Assert.Equal(0, await db.Workflows.CountAsync(w => w.Name == "loadtest-ping-fanout"));
    }
}
