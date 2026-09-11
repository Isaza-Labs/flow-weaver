using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Permission;

public sealed class EffectivePermissions : IEffectivePermissions
{
    private static readonly IReadOnlySet<string> Empty =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    private readonly ICurrentUser _caller;
    private readonly IPermissionGrantReader _reader;

    // Loaded once per scope (request / agent turn) and reused.
    private IReadOnlyList<PermissionGrantRow>? _cache;

    // Transport ceiling (e.g. a messaging channel's cap), resolved once.
    private HashSet<string>? _ceiling;
    private bool _ceilingResolved;

    public EffectivePermissions(ICurrentUser caller, IPermissionGrantReader reader)
    {
        _caller = caller;
        _reader = reader;
    }

    private bool IsAdmin =>
        _caller.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    public Task<bool> HasAsync(string capability, CancellationToken ct = default)
        => HasAsync(capability, PermissionContext.Global, ct);

    public async Task<bool> HasAsync(string capability, PermissionContext ctx, CancellationToken ct = default)
    {
        if (!_caller.IsAuthenticated) return false;
        if (string.IsNullOrEmpty(capability)) return false;

        // Transport ceiling caps everyone, admin included (no escalation by
        // transport). Checked before the admin bypass.
        var ceiling = Ceiling();
        if (ceiling is not null && !ceiling.Contains(capability)) return false;

        if (IsAdmin) return true;                          // admin bypasses everything (within the ceiling)

        foreach (var g in await LoadAsync(ct))
            if (Holds(g.Capabilities, capability)
                && PermissionConditionMatcher.Matches(g.Conditions, ctx))
                return true;
        return false;
    }

    public async Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
    {
        if (!_caller.IsAuthenticated) return Empty;

        HashSet<string> set;
        if (IsAdmin)
            set = CapabilityCatalog.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        else
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in await LoadAsync(ct))
                foreach (var cap in g.Capabilities)
                    set.Add(cap);
        }

        var ceiling = Ceiling();
        if (ceiling is not null) set.IntersectWith(ceiling);
        return set;
    }

    private HashSet<string>? Ceiling()
    {
        if (_ceilingResolved) return _ceiling;
        _ceilingResolved = true;
        var c = _caller.CapabilityCeiling;
        _ceiling = c is null ? null : new HashSet<string>(c, StringComparer.OrdinalIgnoreCase);
        return _ceiling;
    }

    private async Task<IReadOnlyList<PermissionGrantRow>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        _cache = await _reader.GetActiveGrantsForSubjectAsync(_caller.UserId, ct);
        return _cache;
    }

    private static bool Holds(IReadOnlyList<string> caps, string capability)
    {
        foreach (var c in caps)
            if (string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
