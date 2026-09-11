using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public sealed class TraceEventRepository(IServiceScopeFactory scopeFactory) : ITraceEventRepository
{
    public async Task AddAsync(TraceEvent row, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TraceEvents.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateAsync(
        Guid traceEventId, Action<TraceEvent> mutate, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.TraceEvents.FirstOrDefaultAsync(t => t.TraceEventId == traceEventId, ct);
        if (row is null) return false;
        mutate(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
