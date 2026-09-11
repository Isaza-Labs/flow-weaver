using flow_weaver_backend.Models;

namespace flow_weaver_backend.Data.Repositories;

// SSH command catalog reads. Adds the device-type-filtered listing
// (ordered device_type → kind → value, not CreatedAt) and the friendly
// duplicate pre-check the generic CRUD can't express.
public interface IVendorCommandRepository : IRepository<VendorCommand>
{
    // Active catalog rows, optionally narrowed to one device_type,
    // ordered device_type → kind → value.
    Task<IReadOnlyList<VendorCommand>> ListByDeviceTypeAsync(
        string? deviceType, int limit, int offset, CancellationToken ct = default);

    Task<int> CountByDeviceTypeAsync(string? deviceType, CancellationToken ct = default);

    // True when an active row with the same (device_type, kind, value) already
    // exists — the friendly pre-check before the unique index would 500.
    Task<bool> ExistsDuplicateAsync(
        string deviceType, string kind, string value, CancellationToken ct = default);

    // Every active catalog row — the snapshot source the
    // VendorCommandRegistry groups into its per-device-type cache.
    Task<IReadOnlyList<VendorCommand>> GetAllActiveAsync(CancellationToken ct = default);

    // Distinct device_types that have at least one active catalog row. Backs the
    // import resolver's "is this device_type seeded?" check without pulling rows.
    Task<IReadOnlyList<string>> ListActiveDeviceTypesAsync(CancellationToken ct = default);

    // Active EXACT rows for one device_type, projected to (Value, Description,
    // Intent). Patterns are excluded — find_command only ranks runnable
    // commands. Order is unspecified; the handler ranks in memory.
    Task<IReadOnlyList<VendorCommandRank>> ListActiveExactForRankingAsync(
        string deviceType, CancellationToken ct = default);

    // Active rows for one device_type, optionally narrowed to one kind,
    // ordered kind → value, capped at `limit`. Backs list_vendor_commands.
    Task<IReadOnlyList<VendorCommandEntry>> ListByDeviceTypeAndKindAsync(
        string deviceType, string? kind, int limit, CancellationToken ct = default);

    // COUNT of active rows for one device_type (no kind filter) — the
    // "is the listing truncated?" total list_vendor_commands reports.
    Task<int> CountActiveByDeviceTypeAsync(string deviceType, CancellationToken ct = default);
}

// (Value, Description, Intent) projection used by find_command's ranking.
public sealed record VendorCommandRank(string Value, string? Description, string? Intent);

// Catalog entry shape used by list_vendor_commands' exact/pattern split.
public sealed record VendorCommandEntry(
    string Kind, string Value, string? Notes, string Source, string VendorFamily);
