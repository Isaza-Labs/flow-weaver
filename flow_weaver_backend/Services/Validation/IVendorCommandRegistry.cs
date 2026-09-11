namespace flow_weaver_backend.Services.Validation;

// Read-side cache over the vendor_commands table. Loaded on first
// access and refreshed on a short TTL or on explicit invalidation
// triggered by VendorCommandService when the admin edits the catalog.
//
// Exposed as a singleton because the validator runs on every workflow
// create/update and on every call of the validate_ssh_commands AI tool;
// avoiding a fresh DB round-trip on each invocation keeps that path
// cheap. Cache misses fan out to the AppDbContext via IServiceScopeFactory.
public interface IVendorCommandRegistry
{
    // Looks up whether `command` is recognised by any catalog entry for
    // `deviceType`. Returns:
    //   - Known            : exact-match hit OR pattern matched.
    //   - Unknown          : no entry matched.
    //   - DeviceTypeUnknown: no rows at all for this device_type — the
    //                       caller should warn that validation is deferred.
    //   - Skipped          : caller asked us to skip (template, empty).
    Task<KnownStatus> IsKnownAsync(
        string deviceType, string command, CancellationToken ct);

    // Three nearest-neighbour suggestions over the device_type's exact
    // entries. Used to populate "did you mean" warnings. Returns empty
    // when there are no exact entries to compare against.
    Task<IReadOnlyList<string>> SuggestSimilarAsync(
        string deviceType, string command, int max, CancellationToken ct);

    // Convenience: list of every exact command catalogued for a
    // device_type. Powers the agent-facing tool that says "for cisco_ios
    // these are the known commands you can use".
    Task<IReadOnlyList<string>> KnownCommandsForDeviceTypeAsync(
        string deviceType, CancellationToken ct);

    // Drop the cached snapshot. Called by VendorCommandService after every
    // create/update/delete so the next validation sees the change. No-op
    // if there's nothing cached.
    void Invalidate();
}

public enum KnownStatus
{
    Known,
    Unknown,
    DeviceTypeUnknown,
    Skipped,
}
