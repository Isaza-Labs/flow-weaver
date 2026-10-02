using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Tests;

// The device-usage report (GET /api/admin/metrics/devices). The
// commercial "managed device" definition is a pending product decision, so the
// report stays neutral: it exposes the raw breakdown + a count per candidate
// definition, each reproducible by a direct DB query. These tests pin that the
// counts are correct AND that the report matches the direct query it documents.
public class DeviceUsageReportTests
{
    private static readonly DateTime Synced = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static void Seed(AppDbContext db, bool active, bool qa, bool synced, string status, bool fromInventory)
    {
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = "d",
            IsActive = active,
            AllowQa = qa,
            LastSyncAt = synced ? Synced : default,   // default = never synced
            Status = status,
            SourceId = fromInventory ? Guid.NewGuid() : null,
        });
    }

    private static async Task<DeviceUsageResponse> Report(AppDbContext db)
    {
        var controller = new AdminMetricsController(db, new FakeUser ());
        var result = await controller.Devices(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<DeviceUsageResponse>(ok.Value);
    }

    private static int Candidate(DeviceUsageResponse r, string key) =>
        r.CandidateManagedCounts.Single(c => c.Key == key).Count;

    [Fact]
    public async Task Breakdown_and_candidate_counts_are_correct()
    {
        using var db = NewDb(nameof(Breakdown_and_candidate_counts_are_correct));
        //     active  qa     synced status        fromInventory
        Seed(db, true,  false, true,  "reachable",  false);  // 1 manual, synced, reachable
        Seed(db, true,  false, true,  "unknown",    true);   // 2 inventory, synced
        Seed(db, true,  false, false, "",           true);   // 3 inventory, NEVER synced (discovered, untouched)
        Seed(db, true,  true,  true,  "reachable",  false);  // 4 QA lab
        Seed(db, false, false, true,  "reachable",  false);  // 5 INACTIVE
        Seed(db, true,  false, true,  "reachable",  false);  // 6 manual, synced, reachable
        await db.SaveChangesAsync();

        var r = await Report(db);

        Assert.Equal(6, r.Total);
        Assert.Equal(5, r.Breakdown["active"]);
        Assert.Equal(1, r.Breakdown["inactive"]);
        Assert.Equal(1, r.Breakdown["active_qa_lab"]);
        Assert.Equal(4, r.Breakdown["active_non_qa_lab"]);
        Assert.Equal(2, r.Breakdown["active_from_inventory"]);
        Assert.Equal(3, r.Breakdown["active_manual"]);
        Assert.Equal(1, r.Breakdown["active_never_synced"]);
        Assert.Equal(4, r.Breakdown["active_synced"]);

        Assert.Equal(5, Candidate(r, "A_all_active"));
        Assert.Equal(4, Candidate(r, "B_active_excluding_qa_lab"));
        Assert.Equal(3, Candidate(r, "C_active_excluding_qa_and_never_synced"));
        Assert.Equal(2, Candidate(r, "D_active_excluding_qa_reachable"));

        Assert.Equal(3, r.ByStatusActive["reachable"]);
        Assert.Equal(1, r.ByStatusActive["unknown"]);
        Assert.Equal(1, r.ByStatusActive["(unset)"]);
    }

    [Fact]
    public async Task Each_candidate_matches_a_direct_db_query()
    {
        // The whole point: the report IS the query. Reproduce each
        // candidate's documented WHERE directly and assert it equals the report.
        using var db = NewDb(nameof(Each_candidate_matches_a_direct_db_query));
        Seed(db, true, false, true, "reachable", false);
        Seed(db, true, false, false, "unknown", true);
        Seed(db, true, true, true, "reachable", false);
        Seed(db, false, false, true, "reachable", false);
        await db.SaveChangesAsync();

        var r = await Report(db);
        var q = db.Devices.AsQueryable();
        var never = default(DateTime);

        Assert.Equal(await q.CountAsync(d => d.IsActive), Candidate(r, "A_all_active"));
        Assert.Equal(await q.CountAsync(d => d.IsActive && !d.AllowQa), Candidate(r, "B_active_excluding_qa_lab"));
        Assert.Equal(await q.CountAsync(d => d.IsActive && !d.AllowQa && d.LastSyncAt != never),
            Candidate(r, "C_active_excluding_qa_and_never_synced"));
        Assert.Equal(await q.CountAsync(d => d.IsActive && !d.AllowQa && d.Status == "reachable"),
            Candidate(r, "D_active_excluding_qa_reachable"));
    }

}
