using System.Text.Json;
using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// Device persistence. Inherits the generic CRUD; the only specialisation is
// translating the unique source/external-identifier constraint violation into
// a ConflictException (see DeviceRepository) so DeviceService stays free of
// EF Core / Npgsql types.
public interface IDeviceRepository : IRepository<Device>
{
    // By-id lookup with NO active filter. The SSH worker resolves its
    // target device straight from the dispatched step's DeviceId (already
    // authorised by the executor) — mirrors the original handler query, which
    // filtered on DeviceId alone.
    Task<Device?> FindByIdUnscopedAsync(Guid deviceId, CancellationToken ct = default);

    // Active device with this exact (case-insensitive) inventory name. Backs
    // the portable `device` key of ssh / ansible steps (snippets/SPEC.md),
    // which names a device rather than carrying a per-instance id. Exact,
    // never fuzzy — a near miss would run commands on the wrong box.
    Task<Device?> FindActiveByNameAsync(string name, CancellationToken ct = default);

    // Active device whose IpAddress is exactly this address. Backs the ssh
    // `host` alias (snippets/SPEC.md `ssh`), which names an address the
    // handler must resolve to an INVENTORY device: no match is `not_found`,
    // never an ad-hoc connection to whatever answers on that address.
    Task<Device?> FindActiveByIpAsync(string ipAddress, CancellationToken ct = default);

    // Active devices filtered by optional platform/vendor
    // (substring) and site/role (exact), ordered by DeviceName, capped at
    // `limit`. Projects the agent-facing columns plus the raw Properties blob
    // (the query_devices tool extracts `model` from it in memory). Read-only.
    Task<IReadOnlyList<DeviceQueryRow>> QueryActiveAsync(
        string? platform, string? vendor, string? site, string? role,
        int limit, CancellationToken ct = default);

    // Trust-on-first-use: record `fingerprint` as the device's expected SSH
    // host key, but ONLY while the column is still NULL. The null guard is
    // part of the UPDATE's WHERE clause, not a read-then-write in the worker,
    // so two concurrent steps against the same freshly-added device can never
    // race one pin over the other — and, more importantly, an operator's
    // deliberate pin can never be silently overwritten by a later connect.
    //
    // Returns true when this call is the one that pinned it.
    Task<bool> TryPinHostKeyAsync(
        Guid deviceId, string fingerprint, CancellationToken ct = default);
}

// query_devices projection: first-class columns + the custom Properties blob.
public sealed record DeviceQueryRow(
    Guid DeviceId, string DeviceName, string IpAddress, string Platform, string Vendor,
    string Status, string OsVersion, string Role, string Site, JsonElement Properties);
