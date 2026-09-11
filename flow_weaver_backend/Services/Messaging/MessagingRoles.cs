namespace flow_weaver_backend.Services.Messaging;

// Role math for channel turns. The effective role is the MORE RESTRICTIVE of
// the linked user's real role and the channel's MaxRole ceiling — a channel can
// only narrow privileges, never widen them (no escalation by transport).
public static class MessagingRoles
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["viewer"] = 1,
        ["operator"] = 2,
        ["admin"] = 3,
    };

    // min(userRole, maxRole). Null/empty maxRole = no ceiling (user's role wins).
    // Unknown user role defaults to the lowest rank; unknown ceiling to the
    // highest (i.e. "no real ceiling") so a typo never accidentally grants more.
    public static string Effective(string userRole, string? maxRole)
    {
        if (string.IsNullOrWhiteSpace(maxRole)) return Normalize(userRole);
        var u = Rank.GetValueOrDefault(userRole, 1);
        var m = Rank.GetValueOrDefault(maxRole, int.MaxValue);
        return u <= m ? Normalize(userRole) : Normalize(maxRole);
    }

    private static string Normalize(string role) => role.ToLowerInvariant();
}
