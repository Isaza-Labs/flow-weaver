using System.Security.Cryptography;
using System.Text;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Tests;

// Verifies the three signature schemes the receiver supports. Each
// case feeds a known-good HMAC and a tampered variant.
public class GitWebhookSignatureTests
{
    private static string SignGithub(byte[] body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public void Github_valid_signature_passes()
    {
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        var header = SignGithub(body, "s3cret");
        Assert.True(GitWebhookSignature.VerifyGithub(body, header, "s3cret"));
    }

    [Fact]
    public void Github_uppercase_hex_passes()
    {
        // GitHub always lowercases its hex but a custom emitter might
        // not — comparison must be case-insensitive.
        var body = Encoding.UTF8.GetBytes("hello");
        var header = SignGithub(body, "k").ToUpperInvariant();
        Assert.True(GitWebhookSignature.VerifyGithub(body, header, "k"));
    }

    [Fact]
    public void Github_tampered_body_fails()
    {
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        var header = SignGithub(body, "s3cret");
        var tampered = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/evil\"}");
        Assert.False(GitWebhookSignature.VerifyGithub(tampered, header, "s3cret"));
    }

    [Fact]
    public void Github_wrong_secret_fails()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var header = SignGithub(body, "right");
        Assert.False(GitWebhookSignature.VerifyGithub(body, header, "wrong"));
    }

    [Fact]
    public void Github_missing_prefix_fails()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var hexOnly = SignGithub(body, "s")["sha256=".Length..];
        Assert.False(GitWebhookSignature.VerifyGithub(body, hexOnly, "s"));
    }

    [Fact]
    public void Github_null_or_empty_inputs_fail()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.False(GitWebhookSignature.VerifyGithub(body, null, "s"));
        Assert.False(GitWebhookSignature.VerifyGithub(body, "sha256=", "s"));
        Assert.False(GitWebhookSignature.VerifyGithub(body, "sha256=abc", "s"));
        Assert.False(GitWebhookSignature.VerifyGithub(body, SignGithub(body, "s"), string.Empty));
    }

    [Fact]
    public void Gitlab_token_match_passes()
    {
        Assert.True(GitWebhookSignature.VerifyGitlab("super-secret", "super-secret"));
    }

    [Fact]
    public void Gitlab_token_mismatch_fails()
    {
        Assert.False(GitWebhookSignature.VerifyGitlab("super-secret", "other-secret"));
        Assert.False(GitWebhookSignature.VerifyGitlab(null, "s"));
        Assert.False(GitWebhookSignature.VerifyGitlab("s", string.Empty));
    }

    [Fact]
    public void Gitlab_length_mismatch_fails_without_throwing()
    {
        // FixedTimeEquals throws on length mismatch; the wrapper must
        // pre-check length so the verifier returns false cleanly.
        Assert.False(GitWebhookSignature.VerifyGitlab("short", "a-much-longer-secret"));
    }

    [Fact]
    public void Generic_uses_same_scheme_as_github()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.True(GitWebhookSignature.VerifyGeneric(body, SignGithub(body, "x"), "x"));
        Assert.False(GitWebhookSignature.VerifyGeneric(body, SignGithub(body, "x"), "y"));
    }
}
