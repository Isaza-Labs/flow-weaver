using flow_weaver_backend.Services.Security;

namespace flow_weaver_backend.Tests;

// Device.ExpectedSshHostKeyFingerprint existed, SshHandler read it and the
// Python runner enforced it — but nothing could ever SET it: no DTO field, no
// controller path, no UI. So it was always null, the runner's PinPolicy accepted
// whatever key was presented, and every SSH session was open to an on-path
// attacker harvesting the device credential.
//
// These cover the normalisation that makes a pasted pin comparable to what the
// runner computes. The pin must round-trip to the runner's `_normalize_pin`
// form (deploy/python/flow_weaver_ssh_runner.py): prefix- and padding-
// insensitive, base64 of a 32-byte SHA-256 digest.
public class SshHostKeyPinningTests
{
    // ssh-keygen -lf output for a known key: SHA-256 of the empty byte string.
    private const string Canonical = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    [Theory]
    [InlineData("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]   // canonical
    [InlineData("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=")]  // padded
    [InlineData("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")]          // bare digest
    [InlineData("  sha256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU  ")] // pasted with noise
    public void Accepted_spellings_all_normalize_to_the_canonical_form(string input)
    {
        Assert.True(SshHostKeyFingerprint.TryNormalize(input, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(Canonical, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // An MD5 colon-hex fingerprint. Older ssh clients print these; accepting one
    // would store a pin the runner can never match, silently disabling pinning.
    [InlineData("MD5:16:27:ac:a5:76:28:2d:36:63:1b:56:4d:eb:df:a6:48")]
    // Truncated digests are the dangerous case: a short "fingerprint" compares
    // equal for keys that differ, which is precisely the MITM the pin prevents.
    [InlineData("SHA256:47DEQpj8")]
    [InlineData("SHA256:not-base64-at-all-but-exactly-43-characters")]
    public void Malformed_pins_are_rejected(string? input)
    {
        Assert.False(SshHostKeyFingerprint.TryNormalize(input, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.NotNull(error);
    }

    [Fact]
    public void Equivalent_ignores_prefix_and_padding()
    {
        Assert.True(SshHostKeyFingerprint.Equivalent(
            Canonical, "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="));
    }

    [Fact]
    public void Equivalent_is_false_for_different_keys()
    {
        Assert.False(SshHostKeyFingerprint.Equivalent(
            Canonical, "SHA256:zzzEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU"));
    }

    [Fact]
    public void Equivalent_is_false_when_either_side_is_malformed()
    {
        // Never let a garbage stored value compare equal to a real observed key.
        Assert.False(SshHostKeyFingerprint.Equivalent(Canonical, "garbage"));
        Assert.False(SshHostKeyFingerprint.Equivalent(null, Canonical));
    }
}
