using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Security;

// Regex catalog of the secret shapes we expect to find in the wild.
// Intentionally over-eager — a few false positives on long hex strings
// is fine, silent persistence of an API key is not. If a class of real
// tokens starts getting redacted by mistake, tighten the specific regex;
// never relax the global default.
public sealed class SecretRedactor : ISecretRedactor
{
    // (label, pattern). Order matters — more specific patterns come
    // first so a JWT doesn't get re-flagged as a generic base64 blob.
    private static readonly (string Label, Regex Pattern)[] Patterns =
    {
        // Provider-shaped keys
        ("anthropic_key",   new Regex(@"\bsk-ant-(?:api03-)?[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled)),
        ("openai_key",      new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled)),
        ("github_pat",      new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled)),
        ("aws_access_key",  new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("aws_secret",      new Regex(@"(?i)aws(.{0,20})?(secret|access)?(.{0,5})?[=:]\s*['""]?([A-Za-z0-9/+=]{40})['""]?", RegexOptions.Compiled)),
        ("slack_token",     new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,48}\b", RegexOptions.Compiled)),
        ("stripe_key",      new Regex(@"\b(?:sk|pk|rk)_(?:test|live)_[A-Za-z0-9]{24,}\b", RegexOptions.Compiled)),

        // Generic auth headers (inside a longer block of text)
        ("authorization",   new Regex(@"(?i)authorization[\s]*[:=][\s]*(?:bearer\s+)?[A-Za-z0-9\-._~+/]{20,}=*", RegexOptions.Compiled)),
        ("basic_auth",      new Regex(@"(?i)basic\s+[A-Za-z0-9+/]{16,}={0,2}", RegexOptions.Compiled)),

        // JWT (three base64url chunks separated by `.`)
        ("jwt",             new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled)),

        // `password = "…"`, `password: "…"`, `pwd=…` — covers config-style pastes
        ("password_kv",     new Regex(@"(?i)(password|passwd|pwd)[\s]*[=:][\s]*['""]?([^\s'""]{6,})['""]?", RegexOptions.Compiled)),
        ("token_kv",        new Regex(@"(?i)(api[_-]?key|token|secret)[\s]*[=:][\s]*['""]?([A-Za-z0-9_\-]{16,})['""]?", RegexOptions.Compiled)),

        // Long opaque hex (SHA-256, bearer-like tokens) — last resort
        ("hex_40_plus",     new Regex(@"\b[a-f0-9]{40,}\b", RegexOptions.Compiled)),
    };

    public (string redacted, bool matched) Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return (input ?? string.Empty, false);

        var current = input;
        var matched = false;
        foreach (var (label, pattern) in Patterns)
        {
            var replaced = pattern.Replace(current, _ =>
            {
                matched = true;
                return $"[REDACTED:{label}]";
            });
            current = replaced;
        }
        return (current, matched);
    }
}
