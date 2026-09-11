using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Exceptions;
using flow_weaver_backend.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace flow_weaver_backend.Data.Repositories;

// Device persistence. The generic base covers list/get/add/save; this override
// keeps the Postgres-specific duplicate detection (the (source_id, external_id)
// unique indexes) inside the data layer and surfaces it as a ConflictException,
// which DeviceService maps to a 409 without ever referencing DbUpdateException
// or PostgresException.
public class DeviceRepository : RepositoryBase<Device>, IDeviceRepository
{
    public DeviceRepository(AppDbContext db) : base(db)
    {
    }

    public async Task<Device?> FindByIdUnscopedAsync(Guid deviceId, CancellationToken ct = default)
        => await Set.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);

    public async Task<Device?> FindActiveByNameAsync(string name, CancellationToken ct = default)
    {
        var needle = name.Trim().ToLowerInvariant();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(d => d.DeviceName.ToLower() == needle, ct);
    }

    public async Task<Device?> FindActiveByIpAsync(string ipAddress, CancellationToken ct = default)
    {
        var needle = ipAddress.Trim();
        return await Query(activeOnly: true, tracking: false)
            .FirstOrDefaultAsync(d => d.IpAddress == needle, ct);
    }

    public async Task<bool> TryPinHostKeyAsync(
        Guid deviceId, string fingerprint, CancellationToken ct = default)
    {
        // Single UPDATE … WHERE expected_ssh_host_key_fingerprint IS NULL.
        // ExecuteUpdateAsync bypasses the change tracker, which is what we
        // want here: the worker holds a no-tracking Device and must not push
        // any of its other columns back.
        var rows = await Set
            .Where(d => d.DeviceId == deviceId && d.ExpectedSshHostKeyFingerprint == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.ExpectedSshHostKeyFingerprint, fingerprint)
                    .SetProperty(d => d.UpdatedAt, DateTime.UtcNow),
                ct);
        return rows > 0;
    }

    public async Task<IReadOnlyList<DeviceQueryRow>> QueryActiveAsync(
        string? platform, string? vendor, string? site, string? role,
        int limit, CancellationToken ct = default)
    {
        var q = Query(activeOnly: true, tracking: false);

        if (!string.IsNullOrEmpty(platform))
            q = q.Where(d => d.Platform.Contains(platform));
        if (!string.IsNullOrEmpty(vendor))
            q = q.Where(d => d.Vendor.Contains(vendor));
        if (!string.IsNullOrEmpty(site))
            q = q.Where(d => d.Site == site);
        if (!string.IsNullOrEmpty(role))
            q = q.Where(d => d.Role == role);

        return await q
            .OrderBy(d => d.DeviceName)
            .Take(limit)
            .Select(d => new DeviceQueryRow(
                d.DeviceId, d.DeviceName, d.IpAddress, d.Platform, d.Vendor,
                d.Status, d.OsVersion, d.Role, d.Site, d.Properties))
            .ToListAsync(ct);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            return await base.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateDeviceIdentifier(ex))
        {
            throw new ConflictException(
                "A device with the same source/external identifier already exists.");
        }
    }

    private static bool IsDuplicateDeviceIdentifier(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == PostgresErrorCodes.UniqueViolation
        && pg.ConstraintName == "IX_devices_SourceId_ExternalId";
}
