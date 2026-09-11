using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using VendorCommandModel = flow_weaver_backend.Models.VendorCommand;

namespace flow_weaver_backend.Services.Validation;

// Cached read-side over vendor_commands. The snapshot lives for
// `TtlSeconds` between automatic refreshes; mutations on the table
// invalidate the snapshot directly via Invalidate() so the
// catalog admin doesn't have to wait the TTL to see their edit applied.
public sealed class VendorCommandRegistry : IVendorCommandRegistry
{
    private const int TtlSeconds = 60;
    // Per-pattern regex deadline. Same value PolicyEvaluator uses for
    // ssh_command_regex — bad patterns can't stall the validator.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VendorCommandRegistry> _logger;
    private CatalogSnapshot? _cache;

    public VendorCommandRegistry(
        IServiceScopeFactory scopeFactory,
        ILogger<VendorCommandRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Invalidate() => _cache = null;

    public async Task<KnownStatus> IsKnownAsync(
        string deviceType, string command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return KnownStatus.Skipped;
        if (LooksLikeTemplate(command)) return KnownStatus.Skipped;
        if (string.IsNullOrWhiteSpace(deviceType)) return KnownStatus.DeviceTypeUnknown;

        var snap = await GetOrLoadAsync(ct);
        if (!snap.ByDeviceType.TryGetValue(deviceType, out var entry))
            return KnownStatus.DeviceTypeUnknown;

        var normalised = NormaliseExact(command);
        if (entry.ExactCommands.Contains(normalised)) return KnownStatus.Known;

        foreach (var pattern in entry.Patterns)
        {
            try
            {
                if (pattern.IsMatch(normalised)) return KnownStatus.Known;
            }
            catch (RegexMatchTimeoutException)
            {
                // Bad pattern; log and move on. The admin can fix the row.
                _logger.LogWarning(
                    "vendor_command.pattern_timeout device_type={DeviceType}",
                    deviceType);
            }
        }

        return KnownStatus.Unknown;
    }

    public async Task<IReadOnlyList<string>> SuggestSimilarAsync(
        string deviceType, string command, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(deviceType))
            return Array.Empty<string>();

        var snap = await GetOrLoadAsync(ct);
        if (!snap.ByDeviceType.TryGetValue(deviceType, out var entry))
            return Array.Empty<string>();

        var target = NormaliseExact(command);
        return entry.ExactCommands
            .Select(c => (Command: c, Distance: LevenshteinDistance(target, c)))
            .OrderBy(t => t.Distance)
            .Take(Math.Max(0, max))
            .Select(t => t.Command)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> KnownCommandsForDeviceTypeAsync(
        string deviceType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceType)) return Array.Empty<string>();
        var snap = await GetOrLoadAsync(ct);
        if (!snap.ByDeviceType.TryGetValue(deviceType, out var entry))
            return Array.Empty<string>();
        return entry.ExactCommands.ToList();
    }

    private async Task<CatalogSnapshot> GetOrLoadAsync(CancellationToken ct)
    {
        if (_cache is { IsExpired: false } existing)
            return existing;

        // Re-fetch under a lock to avoid a thundering-herd of concurrent
        // workflow saves all hitting the DB after a TTL flip.
        var snap = await LoadSnapshotAsync(ct);
        _cache = snap;
        return snap;
    }

    private async Task<CatalogSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IVendorCommandRepository>();

        var rows = await repo.GetAllActiveAsync(ct);

        var byDeviceType = rows
            .GroupBy(r => r.DeviceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var patterns = new List<Regex>();
                    foreach (var row in g)
                    {
                        if (string.Equals(row.Kind, VendorCommandModel.KindExact, StringComparison.OrdinalIgnoreCase))
                        {
                            exact.Add(row.Value);
                            continue;
                        }
                        if (string.Equals(row.Kind, VendorCommandModel.KindPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                patterns.Add(new Regex(
                                    row.Value,
                                    RegexOptions.IgnoreCase | RegexOptions.Compiled,
                                    RegexTimeout));
                            }
                            catch (ArgumentException ex)
                            {
                                _logger.LogWarning(
                                    "vendor_command.pattern_invalid device_type={DeviceType} pattern={Pattern} error={Error}",
                                    g.Key, row.Value, ex.Message);
                            }
                        }
                    }
                    return new DeviceTypeEntry(exact, patterns);
                },
                StringComparer.OrdinalIgnoreCase);

        return new CatalogSnapshot(byDeviceType, DateTime.UtcNow.AddSeconds(TtlSeconds));
    }

    private static string NormaliseExact(string s)
    {
        var trimmed = s.Trim();
        return Regex.Replace(trimmed, @"\s+", " ").ToLowerInvariant();
    }

    private static bool LooksLikeTemplate(string s)
    {
        var trimmed = s.Trim();
        return (trimmed.Contains("{{", StringComparison.Ordinal) && trimmed.Contains("}}", StringComparison.Ordinal))
            || (trimmed.Contains("${", StringComparison.Ordinal) && trimmed.Contains('}', StringComparison.Ordinal));
    }

    // Cheap textbook implementation. Catalogues stay in the low hundreds
    // per device_type so this never gets exercised at scale.
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private sealed record DeviceTypeEntry(HashSet<string> ExactCommands, List<Regex> Patterns);

    private sealed record CatalogSnapshot(
        IReadOnlyDictionary<string, DeviceTypeEntry> ByDeviceType,
        DateTime ExpiresAt)
    {
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }
}
