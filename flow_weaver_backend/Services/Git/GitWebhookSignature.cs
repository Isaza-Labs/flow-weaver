using System.Security.Cryptography;
using System.Text;

namespace flow_weaver_backend.Services.Git;

// Provider-specific signature verification. All comparisons use
// CryptographicOperations.FixedTimeEquals to defeat timing attacks.
public static class GitWebhookSignature
{
    // GitHub: header X-Hub-Signature-256 = "sha256=<hex>" of HMAC-SHA256(secret, body).
    // Spec: https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries
    public static bool VerifyGithub(byte[] body, string? header, string secret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var hex = header[prefix.Length..].Trim();
        if (hex.Length != 64) return false;
        var expected = HmacSha256Hex(body, secret);
        return ConstantTimeAsciiEquals(expected, hex);
    }

    // GitLab: header X-Gitlab-Token = literal secret (no HMAC).
    // Spec: https://docs.gitlab.com/ee/user/project/integrations/webhooks.html#validate-payloads-by-using-a-secret-token
    public static bool VerifyGitlab(string? header, string secret)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        if (headerBytes.Length != secretBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(headerBytes, secretBytes);
    }

    // Generic: header X-FlowWeaver-Signature = "sha256=<hex>" same as GitHub.
    public static bool VerifyGeneric(byte[] body, string? header, string secret)
        => VerifyGithub(body, header, secret);

    private static string HmacSha256Hex(byte[] body, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, body);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool ConstantTimeAsciiEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.ASCII.GetBytes(a.ToLowerInvariant());
        var bBytes = Encoding.ASCII.GetBytes(b.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
