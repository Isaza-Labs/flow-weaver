namespace flow_weaver_backend.Services.Common;

/// <summary>
/// Stable, human-readable identity for rows that have to be recognisable
/// across FlowWeaver instances.
/// </summary>
/// <remarks>
/// <para>
/// A workflow exported from one instance and imported into another used to
/// carry nothing but GUIDs. Those are per-instance, so the importer could never
/// tell "the NetBox integration you already have" from "an integration I have
/// never seen", and every dependency read as missing — which is how sharing a
/// workflow ended up generating placeholder python snippets instead of wiring
/// the integration that was sitting right there.
/// </para>
/// <para>
/// The slug is derived from the name ONCE, at creation, and then never changes.
/// That immutability is the whole point: renaming "NetBox" to "NetBox (prod)"
/// must not break every workflow bundle already shared with other teams. It is
/// the identity, not a label.
/// </para>
/// </remarks>
public static class Slug
{
    // Long enough to stay readable for real names, short enough to keep the
    // uniqueness suffix visible when one is needed.
    public const int MaxLength = 60;

    /// <summary>
    /// Derives a slug from a display name: lowercase, ASCII alphanumerics and
    /// single dashes. Returns null when the name yields nothing usable (e.g. a
    /// name made entirely of punctuation or non-Latin script), which the caller
    /// must handle rather than storing an empty identity.
    /// </summary>
    public static string? From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var sb = new System.Text.StringBuilder(name.Length);
        var lastWasDash = true;   // leading dashes are suppressed

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            // Deliberately ASCII-only. A slug travels in file names, URLs and
            // YAML keys across machines whose locale and normalisation we do
            // not control; anything outside [a-z0-9-] is a portability risk.
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }

            if (sb.Length >= MaxLength) break;
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// <see cref="From"/>, then made unique against <paramref name="taken"/> by
    /// appending -2, -3, … Falls back to a random suffix when the name yields
    /// no usable slug at all, so a row always ends up with an identity.
    /// </summary>
    public static string Unique(string? name, IReadOnlySet<string> taken)
    {
        var basis = From(name) ?? "item-" + Guid.NewGuid().ToString("N")[..8];
        if (!taken.Contains(basis)) return basis;

        // Trim the base so base+suffix still fits, otherwise two long names
        // that differ only past MaxLength would collide forever.
        for (var n = 2; n < 1000; n++)
        {
            var suffix = "-" + n;
            var head = basis.Length + suffix.Length > MaxLength
                ? basis[..(MaxLength - suffix.Length)].TrimEnd('-')
                : basis;
            var candidate = head + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }

        // 998 collisions on one name is not a real scenario; a random tail
        // beats throwing on a create path.
        return basis[..Math.Min(basis.Length, MaxLength - 9)].TrimEnd('-')
               + "-" + Guid.NewGuid().ToString("N")[..8];
    }
}
