using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Errors;
using flow_weaver_backend.Services.Security;
using flow_weaver_backend.Services.Security.Authorization;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Worker.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace flow_weaver_backend.Controllers;

// Load-test scaffolding: bulk-seed synthetic devices +
// a pool + a representative per-device ping workflow, so a staging box can be
// driven to 50/250/1000/5000 devices and its p95 / throughput / error-rate
// curves read from /api/admin/metrics/slo. ONLY usable when Simulation:Enabled
// (which itself is refused on a production-tier box, see SimulationGuard) — so
// this can never pollute a real inventory or run against real hardware.
[ApiController]
[Route("api/admin/loadtest")]
[HasPermission("device.manage")]
public sealed class LoadTestController : ControllerBase
{
    private const string DeviceMarker = "loadtest";   // Device.ExternalId
    private const string PoolPrefix = "loadtest-pool-";
    private const string SnippetName = "loadtest-ping";
    private const string WorkflowName = "loadtest-ping-fanout";
    private const int MaxSeed = 20000;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly SimulationOptions _sim;
    private readonly ILogger<LoadTestController> _logger;

    public LoadTestController(
        AppDbContext db, ICurrentUser caller,
        IOptions<SimulationOptions> sim, ILogger<LoadTestController> logger)
    {
        _db = db;
        _caller = caller;
        _sim = sim.Value;
        _logger = logger;
    }

    public sealed class SeedRequest
    {
        public int Count { get; set; }
    }

    [HttpPost("seed")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Seed([FromBody] SeedRequest body, CancellationToken ct)
    {
        if (!_sim.Enabled)
            return Problems.Forbidden(
                "load-test seeding requires Simulation:Enabled=true (staging only)", "simulation_disabled");
        if (body.Count is < 1 or > MaxSeed)
            return Problems.BadRequest($"count must be between 1 and {MaxSeed}", "invalid_count");

        var now = DateTime.UtcNow;
        var empty = JsonDocument.Parse("{}").RootElement;

        // Reuse the representative ping snippet + workflow across seeds so the
        // driver always targets the same workflow id.
        var snippetId = await EnsurePingSnippetAsync(now, empty, ct);
        var workflowId = await EnsurePingWorkflowAsync(snippetId, now, empty, ct);

        // Bulk-create the device batch + a pool holding them.
        var poolId = Guid.NewGuid();
        var deviceIds = new List<Guid>(body.Count);
        var devices = new List<Device>(body.Count);
        for (var i = 0; i < body.Count; i++)
        {
            var id = Guid.NewGuid();
            deviceIds.Add(id);
            devices.Add(new Device
            {
                DeviceId = id,
                DeviceName = $"loadtest-dev-{i:D5}",
                IpAddress = $"10.{(i >> 16) & 255}.{(i >> 8) & 255}.{i & 255}",
                Platform = "cisco_ios",
                Vendor = "simulated",
                Status = "reachable",
                ExternalId = DeviceMarker,
                // Allow every environment so synthetic targets resolve no
                // matter which environment the load-test workflow sits in.
                AllowDraft = true,
                AllowQa = true,
                AllowProduction = true,
                Properties = empty,
                LastSyncAt = now,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        _db.Devices.AddRange(devices);
        _db.DevicePools.Add(new DevicePool
        {
            DevicePoolId = poolId,
            Name = $"{PoolPrefix}{body.Count}-{poolId.ToString()[..8]}",
            Description = "Synthetic load-test pool.",
            StaticMembers = deviceIds,
            AllowDraft = true,
            AllowQa = true,
            AllowProduction = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "loadtest.seed devices={Count} pool_id={PoolId} workflow_id={WorkflowId}",
            body.Count, poolId, workflowId);

        return Ok(new
        {
            device_count = body.Count,
            pool_id = poolId,
            workflow_id = workflowId,
            snippet_id = snippetId,
            // Turnkey: POST this to fire one fan-out run over the whole pool.
            run_hint = $"POST /api/workflow/{workflowId}/run  body: {{ \"target_pools\": [\"{poolId}\"] }}",
        });
    }

    [HttpDelete("teardown")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Teardown(CancellationToken ct)
    {
        if (!_sim.Enabled)
            return Problems.Forbidden(
                "load-test teardown requires Simulation:Enabled=true (staging only)", "simulation_disabled");


        var devices = await _db.Devices
            .Where(d => d.ExternalId == DeviceMarker).ToListAsync(ct);
        var pools = await _db.DevicePools
            .Where(p => p.Name.StartsWith(PoolPrefix)).ToListAsync(ct);
        var snippets = await _db.Snippets
            .Where(s => s.Name == SnippetName).ToListAsync(ct);
        var workflows = await _db.Workflows
            .Where(w => w.Name == WorkflowName).ToListAsync(ct);

        _db.Devices.RemoveRange(devices);
        _db.DevicePools.RemoveRange(pools);
        _db.Snippets.RemoveRange(snippets);
        _db.Workflows.RemoveRange(workflows);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "loadtest.teardown devices={Devices} pools={Pools}",
            devices.Count, pools.Count);

        return Ok(new
        {
            removed_devices = devices.Count,
            removed_pools = pools.Count,
            removed_snippets = snippets.Count,
            removed_workflows = workflows.Count,
        });
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private async Task<Guid> EnsurePingSnippetAsync(
        DateTime now, JsonElement empty, CancellationToken ct)
    {
        var existing = await _db.Snippets
            .Where(s => s.Name == SnippetName && s.IsActive)
            .Select(s => (Guid?)s.SnippetId)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var snippetId = Guid.NewGuid();
        _db.Snippets.Add(new Snippet
        {
            SnippetId = snippetId,
            Name = SnippetName,
            Type = "ping",
            Description = "Load-test per-device ping.",
            TargetMode = "per_device",   // fan out to one step_run per pool device
            MaxParallel = 50,
            TimeoutSeconds = 30,
            Verified = true,
            InputSchema = empty,
            OutputSchema = empty,
            RetryPolicy = empty,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
        return snippetId;
    }

    private async Task<Guid> EnsurePingWorkflowAsync(
        Guid snippetId, DateTime now, JsonElement empty, CancellationToken ct)
    {
        var existing = await _db.Workflows
            .Where(w => w.Name == WorkflowName && w.IsActive)
            .Select(w => (Guid?)w.WorkflowId)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var nodes = JsonDocument.Parse(
            "[{\"id\":\"start\",\"snippet_id\":\"__start__\"},"
            + "{\"id\":\"ping\",\"snippet_id\":\"" + snippetId + "\"},"
            + "{\"id\":\"end\",\"snippet_id\":\"__end__\"}]").RootElement.Clone();
        var edges = JsonDocument.Parse(
            "[{\"source\":\"start\",\"target\":\"ping\",\"type\":\"success\"},"
            + "{\"source\":\"ping\",\"target\":\"end\",\"type\":\"success\"}]").RootElement.Clone();

        var workflowId = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            WorkflowId = workflowId,
            Name = WorkflowName,
            Description = "Load-test ping fan-out over a device pool.",
            Version = 1,
            SchemaVersion = "v1",
            Environment = "draft",   // runs on a dev-sandbox worker
            Nodes = nodes,
            Edges = edges,
            InputSchema = empty,
            Metadata = empty,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
        return workflowId;
    }
}
