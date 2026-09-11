using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Data.Repositories;

public class VendorCommandRepository : RepositoryBase<VendorCommand>, IVendorCommandRepository
{
    public VendorCommandRepository(AppDbContext db) : base(db)
    {
    }

    private IQueryable<VendorCommand> FilteredQuery(string? deviceType)
    {
        var q = Query(activeOnly: true, tracking: false);
        if (!string.IsNullOrWhiteSpace(deviceType))
            q = q.Where(v => v.DeviceType == deviceType);
        return q;
    }

    public async Task<IReadOnlyList<VendorCommand>> ListByDeviceTypeAsync(
        string? deviceType, int limit, int offset, CancellationToken ct = default)
        => await FilteredQuery(deviceType)
            .OrderBy(v => v.DeviceType).ThenBy(v => v.Kind).ThenBy(v => v.Value)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountByDeviceTypeAsync(string? deviceType, CancellationToken ct = default)
        => await FilteredQuery(deviceType).CountAsync(ct);

    public async Task<bool> ExistsDuplicateAsync(
        string deviceType, string kind, string value, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .AnyAsync(v => v.DeviceType == deviceType && v.Kind == kind && v.Value == value, ct);

    public async Task<IReadOnlyList<VendorCommand>> GetAllActiveAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListActiveDeviceTypesAsync(CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Select(v => v.DeviceType)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VendorCommandRank>> ListActiveExactForRankingAsync(
        string deviceType, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(v => v.DeviceType == deviceType && v.Kind == VendorCommand.KindExact)
            .Select(v => new VendorCommandRank(v.Value, v.Description, v.Intent))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VendorCommandEntry>> ListByDeviceTypeAndKindAsync(
        string deviceType, string? kind, int limit, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false)
            .Where(v => v.DeviceType == deviceType);
        if (!string.IsNullOrEmpty(kind))
            q = q.Where(v => v.Kind == kind);
        return await q
            .OrderBy(v => v.Kind).ThenBy(v => v.Value)
            .Take(limit)
            .Select(v => new VendorCommandEntry(v.Kind, v.Value, v.Notes, v.Source, v.VendorFamily))
            .ToListAsync(ct);
    }

    public async Task<int> CountActiveByDeviceTypeAsync(string deviceType, CancellationToken ct = default)
        => await Query(activeOnly: true, tracking: false)
            .Where(v => v.DeviceType == deviceType)
            .CountAsync(ct);
}
