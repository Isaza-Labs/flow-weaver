using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.PythonEnvironments;

// Pure helpers shared by the API service, the builder and the tests:
// name validation, requirements validation, credential masking and the
// build hash. Kept free of I/O so every rule is unit-testable.
public static class PythonEnvironmentText
{
    // Directory-safe slug. 3–50 chars, lowercase, digits and inner hyphens.
    private static readonly Regex NameRe =
        new(@"^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$", RegexOptions.Compiled);

    // `uv venv --python` request: a version ("3.11", "3.12.4", "cpython@3.12",
    // ">=3.11") or an interpreter name/path. No whitespace or shell
    // metacharacters — passed via ArgumentList (no shell), so this is defense
    // in depth and keeps the value readable in the UI.
    private static readonly Regex PythonVersionRe =
        new(@"^[A-Za-z0-9@._+/<>=!~,-]{1,64}$", RegexOptions.Compiled);

    // `scheme://userinfo@` — the userinfo is what we mask. Matches tokens
    // (`https://TOKEN@host`) as well as `user:pass@`.
    private static readonly Regex UrlCredentialsRe =
        new(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)(?<userinfo>[^\s/@]+)@", RegexOptions.Compiled);

    public const string Mask = "***";

    // Shown as the interpreter name in step output when no environment was used.
    public const string SystemName = "system";

    public const int MaxRequirementsLength = 64 * 1024;
    public const int MaxDescriptionLength = 1000;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && NameRe.IsMatch(name)
        && !string.Equals(name, SystemName, StringComparison.Ordinal);

    public static bool IsValidPythonVersion(string? version) =>
        !string.IsNullOrEmpty(version) && PythonVersionRe.IsMatch(version);

    // Replaces every URL userinfo (`user:pass@`, `token@`) with `***@`.
    // Applied to requirements, resolved packages and errors before they leave
    // the server (API responses, audit rows, logs).
    public static string? MaskCredentials(string? text) =>
        string.IsNullOrEmpty(text)
            ? text
            : UrlCredentialsRe.Replace(text, m => m.Groups["scheme"].Value + Mask + "@");

    public static bool ContainsMaskedCredentials(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("://" + Mask + "@", StringComparison.Ordinal);

    // An edit form round-trips the MASKED requirements it was given. Restores
    // each still-masked line from the stored text: a masked incoming line is
    // replaced by the stored line whose masked form is identical. A masked
    // line with no such counterpart cannot be restored (the admin edited the
    // URL around the mask, or typed `***` themselves) — that is an error, so
    // the literal `***` is never saved as a credential.
    public static bool TryRestoreMaskedCredentials(
        string incoming, string? stored, out string restored, out string? error)
    {
        error = null;
        if (!ContainsMaskedCredentials(incoming))
        {
            restored = incoming;
            return true;
        }

        var storedByMasked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(stored ?? string.Empty))
        {
            var masked = MaskCredentials(line)!;
            storedByMasked.TryAdd(masked.Trim(), line);
        }

        var sb = new StringBuilder();
        var lines = SplitLines(incoming);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (ContainsMaskedCredentials(line))
            {
                if (!storedByMasked.TryGetValue(line.Trim(), out var original))
                {
                    restored = incoming;
                    error = $"requirements line {i + 1} still contains masked credentials ('{Mask}'). "
                        + "Re-enter the credentials for that URL, or leave the line exactly as it was shown.";
                    return false;
                }
                line = original;
            }
            sb.Append(line);
            if (i < lines.Count - 1) sb.Append('\n');
        }

        restored = sb.ToString();
        return true;
    }

    // Returns null when acceptable, or a message naming the first problem.
    // Lines are passed to `uv pip install -r` as written. Refused:
    //   - `-r` / `-c` (and long forms): they read OTHER files from the worker's
    //     filesystem, which an environment definition has no business naming
    //     (and whose content would surface in the build error).
    //   - an empty file: an environment with nothing pinned is the system
    //     python3 with extra steps — leave the field null instead.
    public static string? ValidateRequirements(string? requirements)
    {
        if (string.IsNullOrWhiteSpace(requirements))
            return "requirements must list at least one package (requirements.txt format).";
        if (requirements.Length > MaxRequirementsLength)
            return $"requirements is too long (max {MaxRequirementsLength} characters).";
        if (requirements.Contains('\0'))
            return "requirements contains a NUL character.";

        var lines = SplitLines(requirements);
        var hasPackage = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("-r") || lower.StartsWith("--requirement")
                || lower.StartsWith("-c") || lower.StartsWith("--constraint"))
                return $"requirements line {i + 1}: nested requirement/constraint files (-r / -c) are not allowed.";
            if (!line.StartsWith('-')) hasPackage = true;
        }

        return hasPackage
            ? null
            : "requirements must list at least one package (requirements.txt format).";
    }

    // Normalises line endings so the same requirements typed on Windows and
    // Linux hash and diff the same.
    public static string NormalizeRequirements(string requirements) =>
        requirements.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";

    // Header line placed above the specs contributed by allowlist rows, so the
    // requirements file the builder writes (and a build error quoting it)
    // shows where they came from.
    public const string IncludedHeader = "# included from the Python packages allowlist";

    // An environment's EFFECTIVE requirements: its own `requirements` plus the
    // pip specs of the allowlist rows that target it. This — not the raw
    // Requirements column — is what the builder installs and hashes, so a
    // build is reproducible from the database and ResolvedPackages records
    // everything that went in.
    //
    // Deterministic: the own text comes first, unchanged (normalised); the
    // included specs follow, trimmed, de-duplicated case-insensitively against
    // each other AND against the lines already in the own text, and sorted
    // ordinally. With nothing to add the result equals the normalised own
    // text, so environments no row targets keep their previous hash.
    //
    // De-duplication is by the exact requirement line (whitespace and case
    // ignored), not by package name: `paramiko>=3.5` from the env and
    // `paramiko` from a row both stay, and uv resolves them together (a real
    // conflict fails the build with uv's own message).
    public static string BuildEffectiveRequirements(string? requirements, IEnumerable<string?> includedSpecs)
    {
        var own = string.IsNullOrWhiteSpace(requirements) ? string.Empty : NormalizeRequirements(requirements);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(own))
        {
            var key = DedupKey(line);
            if (key is not null) seen.Add(key);
        }

        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in includedSpecs)
        {
            var spec = (raw ?? string.Empty).Trim();
            if (spec.Length == 0) continue;
            var key = DedupKey(spec);
            if (key is null || !seen.Add(key)) continue;
            extra.Add(spec);
        }

        if (extra.Count == 0) return own;

        var sb = new StringBuilder(own);
        sb.Append(IncludedHeader).Append('\n');
        foreach (var spec in extra) sb.Append(spec).Append('\n');
        return sb.ToString();
    }

    // Comparison key of a requirement line: comment stripped, whitespace
    // removed, lower-cased. Null for blank / comment-only lines.
    private static string? DedupKey(string line)
    {
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) line = line[..hash];
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        return new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }

    // SHA-256 over what a build depends on (interpreter request +
    // requirements). Hex, lowercase.
    public static string ComputeHash(string? pythonVersion, string requirements)
    {
        var bytes = Encoding.UTF8.GetBytes((pythonVersion ?? string.Empty) + "\n" + requirements);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
}
