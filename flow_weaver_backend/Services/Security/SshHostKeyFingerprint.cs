namespace flow_weaver_backend.Services.Security;

// Parsing + validation for SSH host-key pins stored on
// Device.ExpectedSshHostKeyFingerprint.
//
// Canonical form is `SHA256:<base64-no-padding>` — what `ssh-keygen -lf`
// prints and what the Python runner emits from
// `_sha256_fingerprint` (deploy/python/flow_weaver_ssh_runner.py). The
// runner's `_normalize_pin` strips the prefix and any `=` padding before
// comparing, so this type mirrors that normalisation exactly: a pin that
// round-trips here compares equal there.
//
// Accepted on input (all normalise to the canonical form):
//   SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU
//   SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=
//   47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU
//
// Rejected: MD5 colon-hex fingerprints (`ab:cd:…`), truncated digests, and
// anything that isn't a 32-byte SHA-256 digest. Refusing a short pin matters:
// a 4-byte "fingerprint" would compare equal for keys that differ, which is
// exactly the MITM the pin exists to stop.
public static class SshHostKeyFingerprint
{
    public const string Prefix = "SHA256:";

    // A SHA-256 digest is 32 bytes → 43 base64 chars without padding.
    private const int Base64Length = 43;
    private const int DigestBytes = 32;

    /// <summary>
    /// Normalises a caller-supplied pin to `SHA256:&lt;base64-no-padding&gt;`.
    /// Returns false (with <paramref name="error"/> set) when the value is not
    /// a well-formed SHA-256 host-key fingerprint.
    /// </summary>
    public static bool TryNormalize(string? raw, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "fingerprint is empty";
            return false;
        }

        var body = raw.Trim();
        if (body.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            body = body[Prefix.Length..].Trim();

        // Drop base64 padding so the stored value is byte-identical to what the
        // runner computes, regardless of how the operator pasted it.
        body = body.TrimEnd('=');

        if (body.Length != Base64Length)
        {
            error =
                "expected a SHA-256 host key fingerprint — 'SHA256:<43 base64 chars>' "
                + "(the value `ssh-keygen -lf <key>` prints, and the one reported as "
                + "`host_key_fingerprint` in an ssh step's output). "
                + "MD5 colon-hex fingerprints are not supported.";
            return false;
        }

        // Base64 needs padding to decode; add it back only for the check.
        if (!Convert.TryFromBase64String(body + "=", new byte[DigestBytes + 2], out var written)
            || written != DigestBytes)
        {
            error = "fingerprint is not valid base64 for a 32-byte SHA-256 digest";
            return false;
        }

        normalized = Prefix + body;
        return true;
    }

    /// <summary>
    /// True when the two pins denote the same key. Mirrors the runner's
    /// `_normalize_pin` comparison (prefix- and padding-insensitive).
    /// </summary>
    public static bool Equivalent(string? a, string? b)
    {
        if (!TryNormalize(a, out var na, out _)) return false;
        if (!TryNormalize(b, out var nb, out _)) return false;
        return string.Equals(na, nb, StringComparison.Ordinal);
    }
}
